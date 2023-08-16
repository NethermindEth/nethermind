// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Data;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Polynomial;
using Nethermind.Verkle.Proofs;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree
{
    private Dictionary<byte[], FrE[]> ProofBranchPolynomialCache { get; } = new(Bytes.EqualityComparer);
    private Dictionary<Stem, SuffixPoly> ProofStemPolynomialCache { get; } = new();

    public ExecutionWitness GenerateExecutionWitnessFromStore(byte[][] keys, out Banderwagon rootPoint)
    {
        VerkleTree tree = new(_verkleStateStore, LimboLogs.Instance);
        return tree.GenerateExecutionWitness(keys, out rootPoint);
    }

    public ExecutionWitness GenerateExecutionWitness(byte[][] keys, out Banderwagon rootPoint)
    {
        if (keys.Length == 0)
        {
            rootPoint = default;
            return new ExecutionWitness();
        }
        VerkleProof proof = CreateVerkleProof(keys, out rootPoint);

        SpanDictionary<byte, List<SuffixStateDiff>> stemStateDiff = new(Bytes.SpanEqualityComparer);
        foreach (byte[] key in keys)
        {
            SuffixStateDiff suffixData = new() { Suffix = key[31], CurrentValue = Get(key) };
            if (!stemStateDiff.TryGetValue(key.Slice(0, 31), out List<SuffixStateDiff>? suffixStateDiffList))
            {
                suffixStateDiffList = new();
                stemStateDiff.TryAdd(key.Slice(0, 31), suffixStateDiffList);
            }
            suffixStateDiffList.Add(suffixData);
        }

        List<StemStateDiff> stemStateDiffList = stemStateDiff.Select(stemStateDiffData =>
            new StemStateDiff { Stem = stemStateDiffData.Key, SuffixDiffs = stemStateDiffData.Value }).ToList();

        return new ExecutionWitness { VerkleProof = proof, StateDiff =  stemStateDiffList };
    }

    public VerkleProof CreateVerkleProof(byte[][] keys, out Banderwagon rootPoint)
    {
        if (keys.Length == 0)
        {
            rootPoint = default;
            return new VerkleProof();
        }
        ProofBranchPolynomialCache.Clear();
        ProofStemPolynomialCache.Clear();

        Dictionary<Stem, byte> depthsByStem = new();
        Dictionary<Stem, ExtPresent> extStatus = new();

        // generate prover path for keys
        Dictionary<byte[], HashSet<byte>> neededOpenings = new(Bytes.EqualityComparer);
        HashSet<byte[]> stemList = new(Bytes.EqualityComparer);

        foreach (byte[] key in keys)
        {
            for (int i = 0; i < 32; i++)
            {
                byte[] parentPath = key[..i];
                InternalNode? node = GetInternalNode(parentPath);
                if (node != null)
                {
                    switch (node.NodeType)
                    {
                        case VerkleNodeType.BranchNode:
                            CreateBranchProofPolynomialIfNotExist(parentPath);
                            neededOpenings.TryAdd(parentPath, new HashSet<byte>());
                            neededOpenings[parentPath].Add(key[i]);
                            continue;
                        case VerkleNodeType.StemNode:
                            Stem keyStem = key[..31];
                            depthsByStem.TryAdd(keyStem, (byte)i);
                            CreateStemProofPolynomialIfNotExist(keyStem);
                            neededOpenings.TryAdd(parentPath, new HashSet<byte>());
                            stemList.Add(parentPath);
                            if (keyStem == node.Stem)
                            {
                                neededOpenings[parentPath].Add(key[31]);
                                extStatus.TryAdd(keyStem, ExtPresent.Present);
                            }
                            else
                            {
                                extStatus.TryAdd(keyStem, ExtPresent.DifferentStem);
                            }
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                else
                {
                    byte[] keyStem = key[..31];
                    extStatus.TryAdd(keyStem, ExtPresent.None);
                    depthsByStem.TryAdd(keyStem, (byte)(i));
                }
                // reaching here means end of the path for the leaf
                break;
            }
        }

        VerkleProof finalProof = CreateProofStruct(stemList, neededOpenings, addLeafOpenings: true, out rootPoint);
        finalProof.VerifyHint.Depths = depthsByStem.Values.ToArray();
        finalProof.VerifyHint.ExtensionPresent = extStatus.Values.ToArray();

        return finalProof;
    }

    public VerkleProof CreateVerkleRangeProof(Stem startStem, Stem endStem, out Banderwagon rootPoint)
    {
        ProofBranchPolynomialCache.Clear();
        ProofStemPolynomialCache.Clear();

        Dictionary<Stem, byte> depthsByStem = new();
        ExtPresent[] extStatus = new ExtPresent[2];

        // generate prover path for keys
        Dictionary<byte[], HashSet<byte>> neededOpenings = new(Bytes.EqualityComparer);
        HashSet<byte[]> stemList = new(Bytes.EqualityComparer);

        int prefixLength = 0;
        while (prefixLength<startStem.Bytes.Length)
        {
            if (startStem.Bytes[prefixLength] != endStem.Bytes[prefixLength]) break;
            prefixLength++;
        }

        int keyIndex = 0;
        foreach (Stem stem in new[] { startStem, endStem })
        {
            for (int i = 0; i < 32; i++)
            {
                if(keyIndex == 1 && i <= prefixLength) continue;
                byte[] parentPath = stem.Bytes[..i];
                InternalNode? node = GetInternalNode(parentPath);
                if (node != null)
                {
                    switch (node.NodeType)
                    {
                        case VerkleNodeType.BranchNode:
                            CreateBranchProofPolynomialIfNotExist(parentPath);
                            neededOpenings.TryAdd(parentPath, new HashSet<byte>());
                            if (i < prefixLength)
                            {
                                neededOpenings[parentPath].Add(startStem.Bytes[i]);
                                continue;
                            }

                            int startIndex = startStem.Bytes[i];
                            int endIndex = endStem.Bytes[i];
                            if (i > prefixLength)
                            {
                                if (keyIndex == 0) endIndex = 255;
                                else startIndex = 0;
                            }

                            for (int j = startIndex; j <= endIndex; j++)
                            {
                                neededOpenings[parentPath].Add((byte)j);
                            }
                            continue;
                        case VerkleNodeType.StemNode:
                            Stem keyStem = stem;
                            depthsByStem.TryAdd(keyStem, (byte)i);
                            CreateStemProofPolynomialIfNotExist(keyStem);
                            neededOpenings.TryAdd(parentPath, new HashSet<byte>());
                            stemList.Add(parentPath);
                            if (keyStem == node.Stem)
                            {
                                neededOpenings[parentPath].Add(0);
                                extStatus[keyIndex++] = ExtPresent.Present;
                            }
                            else
                            {
                                extStatus[keyIndex++] = ExtPresent.DifferentStem;
                            }
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                else
                {
                    Stem keyStem = stem;
                    extStatus[keyIndex++] = ExtPresent.None;
                    depthsByStem.TryAdd(keyStem, (byte)(i));
                }
                // reaching here means end of the path for the leaf
                break;
            }
        }

        VerkleProof finalProof = CreateProofStruct(stemList, neededOpenings, addLeafOpenings: false, out rootPoint);
        finalProof.VerifyHint.Depths = depthsByStem.Values.ToArray();
        finalProof.VerifyHint.ExtensionPresent = extStatus;

        return finalProof;
    }


    private VerkleProof CreateProofStruct(IReadOnlySet<byte[]> stemList, Dictionary<byte[], HashSet<byte>> neededOpenings, bool addLeafOpenings, out Banderwagon rootPoint)
    {

        List<VerkleProverQuery> queries = new();
        HashSet<byte[]> stemWithNoProofSet = new(Bytes.EqualityComparer);
        List<Banderwagon> sortedCommitments = new();

        foreach (KeyValuePair<byte[], HashSet<byte>> elem in neededOpenings)
        {
            if (stemList.Contains(elem.Key))
            {
                InternalNode? suffix = GetInternalNode(elem.Key);
                bool stemWithNoProof = AddStemCommitmentsOpenings(suffix, elem.Value, queries, addLeafOpenings);
                if (stemWithNoProof) stemWithNoProofSet.Add(suffix.Stem.Bytes);
                continue;
            }

            AddBranchCommitmentsOpening(elem.Key, elem.Value, queries);
        }

        VerkleProverQuery root = queries.First();

        rootPoint = root.NodeCommitPoint;
        foreach (VerkleProverQuery query in queries.Where(query => root.NodeCommitPoint != query.NodeCommitPoint))
        {
            if(sortedCommitments.Count == 0 || sortedCommitments[^1] != query.NodeCommitPoint)
                sortedCommitments.Add(query.NodeCommitPoint);
        }

        MultiProof proofConstructor = new(CRS.Instance, PreComputedWeights.Instance);


        Transcript proverTranscript = new("vt");
        VerkleProofStruct proof = proofConstructor.MakeMultiProof(proverTranscript, queries);

        return new VerkleProof
        {
            CommsSorted = sortedCommitments.ToArray(),
            Proof = proof,
            VerifyHint = new VerificationHint
            {
                DifferentStemNoProof = stemWithNoProofSet.ToArray(),
            }
        };
    }

     private void AddBranchCommitmentsOpening(byte[] branchPath, IEnumerable<byte> branchChild, List<VerkleProverQuery> queries)
    {
        if (!ProofBranchPolynomialCache.TryGetValue(branchPath, out FrE[] poly)) throw new EvaluateException();
        InternalNode? node = GetInternalNode(branchPath);
        queries.AddRange(branchChild.Select(childIndex => new VerkleProverQuery(new LagrangeBasis(poly), node!.InternalCommitment.Point, childIndex, poly[childIndex])));
    }

    private bool AddStemCommitmentsOpenings(InternalNode? suffix, HashSet<byte> stemChild, List<VerkleProverQuery> queries, bool addLeafOpenings)
    {
        byte[] stemPath = suffix!.Stem!.Bytes;
        AddExtensionCommitmentOpenings(stemPath, addLeafOpenings? stemChild: new byte[]{}, suffix, queries);
        if (stemChild.Count == 0) return true;

        // this is used for sync proofs - we dont need to include proofs for leaf openings as we send all the leafs
        // the client can generate the leaf and verify the commitments
        if (!addLeafOpenings) return false;

        ProofStemPolynomialCache.TryGetValue(stemPath, out SuffixPoly hashStruct);

        FrE[] c1Hashes = hashStruct.C1;
        FrE[] c2Hashes = hashStruct.C2;

        foreach (byte valueIndex in stemChild)
        {
            int valueLowerIndex = 2 * (valueIndex % 128);
            int valueUpperIndex = valueLowerIndex + 1;

            (FrE valueLow, FrE valueHigh) = VerkleUtils.BreakValueInLowHigh(Get(stemPath.Append(valueIndex).ToArray()));

            int offset = valueIndex < 128 ? 0 : 128;

            Banderwagon commitment;
            FrE[] poly;
            switch (offset)
            {
                case 0:
                    commitment = suffix.C1.Point;
                    poly = c1Hashes.ToArray();
                    break;
                case 128:
                    commitment = suffix.C2.Point;
                    poly = c2Hashes.ToArray();
                    break;
                default:
                    throw new Exception("unreachable");
            }

            VerkleProverQuery openAtValLow = new(new LagrangeBasis(poly), commitment, (byte)valueLowerIndex, valueLow);
            VerkleProverQuery openAtValUpper = new(new LagrangeBasis(poly), commitment, (byte)valueUpperIndex, valueHigh);

            queries.Add(openAtValLow);
            queries.Add(openAtValUpper);
        }

        return false;
    }

    private static void AddExtensionCommitmentOpenings(Stem stem, IEnumerable<byte> value, InternalNode suffix, List<VerkleProverQuery> queries)
    {
        FrE[] extPoly = new FrE[256];
        for (int i = 0; i < 256; i++)
        {
            extPoly[i] = FrE.Zero;
        }
        extPoly[0] = FrE.One;
        extPoly[1] = FrE.FromBytesReduced(stem.Bytes.Reverse().ToArray());
        extPoly[2] = suffix.C1!.PointAsField;
        extPoly[3] = suffix.C2!.PointAsField;

        VerkleProverQuery openAtOne = new(new LagrangeBasis(extPoly), suffix.InternalCommitment.Point, 0, FrE.One);
        VerkleProverQuery openAtStem = new(new LagrangeBasis(extPoly), suffix.InternalCommitment.Point, 1, FrE.FromBytesReduced(stem.Bytes.Reverse().ToArray()));
        queries.Add(openAtOne);
        queries.Add(openAtStem);

        bool openC1 = false;
        bool openC2 = false;
        foreach (byte valueIndex in value)
        {
            if (valueIndex < 128) openC1 = true;
            else openC2 = true;
        }

        if (openC1)
        {
            VerkleProverQuery openAtC1 = new(new LagrangeBasis(extPoly), suffix.InternalCommitment.Point, 2, suffix.C1.PointAsField);
            queries.Add(openAtC1);
        }

        if (openC2)
        {
            VerkleProverQuery openAtC2 = new(new LagrangeBasis(extPoly), suffix.InternalCommitment.Point, 3, suffix.C2.PointAsField);
            queries.Add(openAtC2);
        }
    }

    private void BatchCreateBranchProofPolynomialIfNotExist(HashSet<byte[]> paths)
    {
        Banderwagon[] commitments = new Banderwagon[256 * paths.Count];

        int commitmentIndex = 0;

        foreach (byte[] path in paths)
        {
            for (int i = 0; i < 256; i++)
            {
                InternalNode? node = GetInternalNode(path.Append((byte)i).ToArray());
                commitments[commitmentIndex++] = node == null ? Banderwagon.Identity : node.InternalCommitment.Point;
            }
        }

        Span<FrE> scalars = Banderwagon.BatchMapToScalarField(commitments);

        foreach (byte[] path in paths)
        {
            ProofBranchPolynomialCache[path] = scalars[..256].ToArray();
            scalars = scalars[256..];
        }
    }

    private void CreateBranchProofPolynomialIfNotExist(byte[] path)
    {
        if (ProofBranchPolynomialCache.ContainsKey(path)) return;
        Banderwagon[] commitments = new Banderwagon[256];
        for (int i = 0; i < 256; i++)
        {
            InternalNode? node = GetInternalNode(path.Append((byte)i).ToArray());
            commitments[i] = node == null ? Banderwagon.Identity : node.InternalCommitment.Point;
        }
        ProofBranchPolynomialCache[path] = Banderwagon.BatchMapToScalarField(commitments);
    }

    private void CreateStemProofPolynomialIfNotExist(Stem stem)
    {
        if (ProofStemPolynomialCache.ContainsKey(stem)) return;

        List<FrE> c1Hashes = new(256);
        List<FrE> c2Hashes = new(256);
        for (int i = 0; i < 128; i++)
        {
            (FrE valueLow, FrE valueHigh) = VerkleUtils.BreakValueInLowHigh(Get(stem.Bytes.Append((byte)i).ToArray()));
            c1Hashes.Add(valueLow);
            c1Hashes.Add(valueHigh);
        }

        for (int i = 128; i < 256; i++)
        {
            (FrE valueLow, FrE valueHigh) = VerkleUtils.BreakValueInLowHigh(Get(stem.Bytes.Append((byte)i).ToArray()));
            c2Hashes.Add(valueLow);
            c2Hashes.Add(valueHigh);
        }
        ProofStemPolynomialCache[stem] = new SuffixPoly()
        {
            C1 = c1Hashes.ToArray(),
            C2 = c2Hashes.ToArray()
        };
    }
}
