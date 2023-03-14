// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Data;
using System.Diagnostics;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Polynomial;
using Nethermind.Verkle.Proofs;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Utils;

namespace Nethermind.Verkle.Tree.Proofs;

public class VerkleProver
{
    private readonly IVerkleStore _stateDb;
    private readonly Dictionary<byte[], FrE[]> _proofBranchPolynomialCache = new(Bytes.EqualityComparer);
    private readonly Dictionary<byte[], SuffixPoly> _proofStemPolynomialCache = new(Bytes.EqualityComparer);

    public VerkleProver(IDbProvider dbProvider)
    {
        VerkleStateStore stateDb = new(dbProvider);
        _stateDb = new CompositeVerkleStateStore(stateDb);
    }

    public VerkleProver(IVerkleStore stateStore)
    {
        _stateDb = new CompositeVerkleStateStore(stateStore);
    }

    public VerkleProof CreateVerkleProof(List<byte[]> keys, out Banderwagon rootPoint)
    {
        _proofBranchPolynomialCache.Clear();
        _proofStemPolynomialCache.Clear();

        HashSet<Banderwagon> commsSorted = new();
        SortedDictionary<byte[], byte> depthsByStem = new(Bytes.Comparer);
        SortedDictionary<byte[], ExtPresent> extPresentByStem = new(Bytes.Comparer);

        List<byte[]> extPresent = new();
        List<byte[]> extNone = new();
        List<byte[]> extDifferent = new();

        // generate prover path for keys
        Dictionary<byte[], SortedSet<byte>> neededOpenings = new(Bytes.EqualityComparer);

        foreach (byte[] key in keys)
        {
            Debug.Assert(key.Length == 32);
            for (int i = 0; i < 32; i++)
            {
                byte[] currentPath = key[..i];
                InternalNode? node = _stateDb.GetBranch(currentPath);
                if (node != null)
                {
                    switch (node.NodeType)
                    {
                        case NodeType.BranchNode:
                            CreateBranchProofPolynomialIfNotExist(currentPath);
                            neededOpenings.TryAdd(currentPath, new SortedSet<byte>());
                            neededOpenings[currentPath].Add(key[i]);
                            continue;
                        case NodeType.StemNode:
                            byte[] keyStem = key[..31];
                            depthsByStem.TryAdd(keyStem, (byte)i);
                            CreateStemProofPolynomialIfNotExist(keyStem);
                            neededOpenings.TryAdd(keyStem, new SortedSet<byte>());
                            if (keyStem.SequenceEqual(node.Stem))
                            {
                                neededOpenings[keyStem].Add(key[31]);
                                extPresent.Add(key[..31]);
                            }
                            else
                            {
                                extDifferent.Add(key[..31]);
                            }
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                else
                {
                    extNone.Add(key[..31]);
                }
                // reaching here means end of the path for the leaf
                break;
            }
        }

        List<VerkleProverQuery> queries = new();
        SortedSet<byte[]> stemWithNoProofSet = new();

        foreach (KeyValuePair<byte[], SortedSet<byte>> elem in neededOpenings)
        {
            int pathLength = elem.Key.Length;

            if (pathLength == 31)
            {
                queries.AddRange(AddStemCommitmentsOpenings(elem.Key, elem.Value, out bool stemWithNoProof));
                if (stemWithNoProof) stemWithNoProofSet.Add(elem.Key);
                continue;
            }

            queries.AddRange(AddBranchCommitmentsOpening(elem.Key, elem.Value));
        }

        VerkleProverQuery root = queries.First();

        rootPoint = root._nodeCommitPoint;
        foreach (VerkleProverQuery query in queries.Where(query => root._nodeCommitPoint != query._nodeCommitPoint))
        {
            commsSorted.Add(query._nodeCommitPoint);
        }

        MultiProof proofConstructor = new(CRS.Instance, PreComputeWeights.Init());


        Transcript proverTranscript = new("vt");
        VerkleProofStruct proof = proofConstructor.MakeMultiProof(proverTranscript, queries);

        foreach (byte[] stem in extPresent)
        {
            extPresentByStem.TryAdd(stem, ExtPresent.Present);
        }

        foreach (byte[] stem in extDifferent)
        {
            extPresentByStem.TryAdd(stem, ExtPresent.DifferentStem);
        }

        foreach (byte[] stem in extNone)
        {
            extPresentByStem.TryAdd(stem, ExtPresent.None);
        }

        return new VerkleProof
        {
            CommsSorted = commsSorted.ToArray(),
            Proof = proof,
            VerifyHint = new VerificationHint
            {
                Depths = depthsByStem.Values.ToArray(),
                DifferentStemNoProof = stemWithNoProofSet.ToArray(),
                ExtensionPresent = extPresentByStem.Values.ToArray()
            }
        };
    }

    private IEnumerable<VerkleProverQuery> AddBranchCommitmentsOpening(byte[] branchPath, IEnumerable<byte> branchChild)
    {
        List<VerkleProverQuery> queries = new();
        if (!_proofBranchPolynomialCache.TryGetValue(branchPath, out FrE[] poly)) throw new EvaluateException();
        InternalNode? node = _stateDb.GetBranch(branchPath);
        queries.AddRange(branchChild.Select(childIndex => new VerkleProverQuery(new LagrangeBasis(poly), node!._internalCommitment.Point, FrE.SetElement(childIndex), poly[childIndex])));
        return queries;
    }

    private IEnumerable<VerkleProverQuery> AddStemCommitmentsOpenings(byte[] stemPath, SortedSet<byte> stemChild, out bool stemWithNoProof)
    {
        stemWithNoProof = false;
        List<VerkleProverQuery> queries = new();
        SuffixTree? suffix = _stateDb.GetStem(stemPath);
        queries.AddRange(AddExtensionCommitmentOpenings(stemPath, stemChild, suffix));
        if (stemChild.Count == 0)
        {
            stemWithNoProof = true;
            return queries;
        }


        _proofStemPolynomialCache.TryGetValue(stemPath, out SuffixPoly hashStruct);

        FrE[] c1Hashes = hashStruct.c1;
        FrE[] c2Hashes = hashStruct.c2;

        foreach (byte valueIndex in stemChild)
        {
            int valueLowerIndex = 2 * (valueIndex % 128);
            int valueUpperIndex = valueLowerIndex + 1;

            (FrE valueLow, FrE valueHigh) = VerkleUtils.BreakValueInLowHigh(_stateDb.GetLeaf(stemPath.Append(valueIndex).ToArray()));

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

            VerkleProverQuery openAtValLow = new(new LagrangeBasis(poly), commitment, FrE.SetElement(valueLowerIndex), valueLow);
            VerkleProverQuery openAtValUpper = new(new LagrangeBasis(poly), commitment, FrE.SetElement(valueUpperIndex), valueHigh);

            queries.Add(openAtValLow);
            queries.Add(openAtValUpper);
        }

        return queries;
    }



    private IEnumerable<VerkleProverQuery> OpenBranchCommitment(Dictionary<byte[], SortedSet<byte>> branchProof)
    {
        List<VerkleProverQuery> queries = new();
        foreach (KeyValuePair<byte[], SortedSet<byte>> proofData in branchProof)
        {
            if (!_proofBranchPolynomialCache.TryGetValue(proofData.Key, out FrE[] poly)) throw new EvaluateException();
            InternalNode? node = _stateDb.GetBranch(proofData.Key);
            queries.AddRange(proofData.Value.Select(childIndex => new VerkleProverQuery(new LagrangeBasis(poly), node!._internalCommitment.Point, FrE.SetElement(childIndex), poly[childIndex])));
        }
        return queries;
    }

    private IEnumerable<VerkleProverQuery> OpenStemCommitment(Dictionary<byte[], SortedSet<byte>> stemProof, out List<byte[]> stemWithNoProof)
    {
        stemWithNoProof = new List<byte[]>();
        List<VerkleProverQuery> queries = new();

        foreach (KeyValuePair<byte[], SortedSet<byte>> proofData in stemProof)
        {
            SuffixTree? suffix = _stateDb.GetStem(proofData.Key);
            queries.AddRange(AddExtensionCommitmentOpenings(proofData.Key, proofData.Value, suffix));
            if (proofData.Value.Count == 0)
            {
                stemWithNoProof.Add(proofData.Key);
                continue;
            }

            _proofStemPolynomialCache.TryGetValue(proofData.Key, out SuffixPoly hashStruct);

            FrE[] c1Hashes = hashStruct.c1;
            FrE[] c2Hashes = hashStruct.c2;

            foreach (byte valueIndex in proofData.Value)
            {
                int valueLowerIndex = 2 * (valueIndex % 128);
                int valueUpperIndex = valueLowerIndex + 1;

                (FrE valueLow, FrE valueHigh) = VerkleUtils.BreakValueInLowHigh(_stateDb.GetLeaf(proofData.Key.Append(valueIndex).ToArray()));

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

                VerkleProverQuery openAtValLow = new(new LagrangeBasis(poly), commitment, FrE.SetElement(valueLowerIndex), valueLow);
                VerkleProverQuery openAtValUpper = new(new LagrangeBasis(poly), commitment, FrE.SetElement(valueUpperIndex), valueHigh);

                queries.Add(openAtValLow);
                queries.Add(openAtValUpper);
            }

        }

        return queries;
    }

    private IEnumerable<VerkleProverQuery> AddExtensionCommitmentOpenings(byte[] stem, IEnumerable<byte> value, SuffixTree? suffix)
    {
        List<VerkleProverQuery> queries = new();
        FrE[] extPoly = new FrE[256];
        for (int i = 0; i < 256; i++)
        {
            extPoly[i] = FrE.Zero;
        }
        extPoly[0] = FrE.One;
        extPoly[1] = FrE.FromBytesReduced(stem.Reverse().ToArray());
        extPoly[2] = suffix.C1.PointAsField;
        extPoly[3] = suffix.C2.PointAsField;

        VerkleProverQuery openAtOne = new(new LagrangeBasis(extPoly), suffix.ExtensionCommitment.Point, FrE.SetElement(0), FrE.One);
        VerkleProverQuery openAtStem = new(new LagrangeBasis(extPoly), suffix.ExtensionCommitment.Point, FrE.SetElement(1), FrE.FromBytesReduced(stem.Reverse().ToArray()));
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
            VerkleProverQuery openAtC1 = new(new LagrangeBasis(extPoly), suffix.ExtensionCommitment.Point, FrE.SetElement(2), suffix.C1.PointAsField);
            queries.Add(openAtC1);
        }

        if (openC2)
        {
            VerkleProverQuery openAtC2 = new(new LagrangeBasis(extPoly), suffix.ExtensionCommitment.Point, FrE.SetElement(3), suffix.C2.PointAsField);
            queries.Add(openAtC2);
        }

        return queries;
    }



    private void CreateBranchProofPolynomialIfNotExist(byte[] path)
    {
        if (_proofBranchPolynomialCache.ContainsKey(path)) return;

        FrE[] newPoly = new FrE[256];
        for (int i = 0; i < 256; i++)
        {
            InternalNode? node = _stateDb.GetBranch(path.Append((byte)i).ToArray());
            newPoly[i] = node == null ? FrE.Zero : node._internalCommitment.PointAsField;
        }
        _proofBranchPolynomialCache[path] = newPoly;
    }

    private void CreateStemProofPolynomialIfNotExist(byte[] stem)
    {
        if (_proofStemPolynomialCache.ContainsKey(stem)) return;

        List<FrE> c1Hashes = new(256);
        List<FrE> c2Hashes = new(256);
        for (int i = 0; i < 128; i++)
        {
            (FrE valueLow, FrE valueHigh) = VerkleUtils.BreakValueInLowHigh(_stateDb.GetLeaf(stem.Append((byte)i).ToArray()));
            c1Hashes.Add(valueLow);
            c1Hashes.Add(valueHigh);
        }

        for (int i = 128; i < 256; i++)
        {
            (FrE valueLow, FrE valueHigh) = VerkleUtils.BreakValueInLowHigh(_stateDb.GetLeaf(stem.Append((byte)i).ToArray()));
            c2Hashes.Add(valueLow);
            c2Hashes.Add(valueHigh);
        }
        _proofStemPolynomialCache[stem] = new SuffixPoly()
        {
            c1 = c1Hashes.ToArray(),
            c2 = c2Hashes.ToArray()
        };
    }

}
