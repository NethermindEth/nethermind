// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core.Verkle;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Polynomial;
using Nethermind.Verkle.Proofs;
using Nethermind.Verkle.Tree.Proofs;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree
{
    private static Stem[] GetStemsFromKeys(Span<byte[]> keys, int numberOfStems)
    {
        Stem[] stems = new Stem[numberOfStems];
        stems[0] = keys[0][..31];

        int stemIndex = 1;
        for (int i = 1; i < keys.Length; i++)
        {
            byte[] currentStem = keys[i][..31];
            if (stems[stemIndex - 1].Equals(currentStem)) continue;
            stems[stemIndex++] = currentStem;
        }
        return stems;
    }

    private static Stem[] GetStemsFromStemStateDiff(List<StemStateDiff> suffixDiffs)
    {
        Stem[] stems = new Stem[suffixDiffs.Count];
        for (int i = 0; i < suffixDiffs.Count; i++)
        {
            stems[i] = suffixDiffs[i].Stem;
        }
        return stems;
    }

    public static bool VerifyVerkleProof(ExecutionWitness execWitness, Banderwagon root, [NotNullWhen(true)]out UpdateHint? updateHint)
    {
        // var logg = SimpleConsoleLogger.Instance;
        updateHint = null;
        int numberOfStems = execWitness.VerkleProof.DepthExtensionPresent.Length;

        // sorted commitments including root
        Banderwagon[] commSortedByPath = new Banderwagon[execWitness.VerkleProof.CommitmentsByPath.Length + 1];
        commSortedByPath[0] = root;
        execWitness.VerkleProof.CommitmentsByPath.CopyTo(commSortedByPath, 1);

        Stem[] stems = GetStemsFromStemStateDiff(execWitness.StateDiff);

        Dictionary<Stem, (ExtPresent, byte)> depthsAndExtByStem = new();
        HashSet<Stem> stemsWithExtension = new();
        for (int i = 0; i < numberOfStems; i++)
        {
            byte extAndDepth = execWitness.VerkleProof.DepthExtensionPresent[i];
            byte depth = (byte)(extAndDepth >> 3);
            ExtPresent extPresent = (ExtPresent)(extAndDepth & 3);
            depthsAndExtByStem.Add(stems[i], (extPresent, depth));
            if (extPresent == ExtPresent.Present) stemsWithExtension.Add(stems[i]);
        }

        SortedSet<List<byte>> allPaths = new(new ListComparer());
        SortedSet<(List<byte>, byte)> allPathsAndZs = new(new ListWithByteComparer());
        Dictionary<(List<byte>, byte), FrE> leafValuesByPathAndZ = new(new ListWithByteEqualityComparer());
        SortedDictionary<List<byte>, Stem> otherStemsByPrefix = new(new ListComparer());

        foreach (StemStateDiff stemStateDiff in execWitness.StateDiff)
        {
            Stem stem = stemStateDiff.Stem;
            (ExtPresent extPres, byte depth) = depthsAndExtByStem[stem];

            for (int i = 0; i < depth; i++)
            {
                allPaths.Add(new List<byte>(stem.Bytes[..i]));
                allPathsAndZs.Add((new List<byte>(stem.Bytes[..i]), stem.Bytes[i]));
            }

            switch (extPres)
            {
                case ExtPresent.DifferentStem:
                    allPaths.Add(new List<byte>(stem.Bytes[..depth]));
                    allPathsAndZs.Add((new List<byte>(stem.Bytes[..depth]), 0));
                    allPathsAndZs.Add((new List<byte>(stem.Bytes[..depth]), 1));

                    Stem otherStem;

                    // find the stems that are equal to the stem we are assuming to be without extension
                    // this happens when we initially added this stem when we were searching for another one
                    // but then in a future key, we found that we needed this stem too.
                    Stem[] found = stemsWithExtension.Where(x => x.BytesAsSpan[..depth].SequenceEqual(stem.Bytes[..depth])).ToArray();

                    switch (found.Length)
                    {
                        case 0:
                            found = execWitness.VerkleProof.OtherStems.Where(x => x.BytesAsSpan[..depth].SequenceEqual(stem.Bytes[..depth])).ToArray();
                            Stem encounteredStem = found[^1];
                            otherStem = encounteredStem;

                            // Add extension node to proof in particular, we only want to open at (1, stem)
                            leafValuesByPathAndZ[(new List<byte>(stem.Bytes[..depth]), 0)] = FrE.One;
                            leafValuesByPathAndZ.TryAdd((new List<byte>(stem.Bytes[..depth]), 1), FrE.FromBytesReduced(encounteredStem.Bytes.Reverse().ToArray()));
                            break;
                        case 1:
                            otherStem = found[0];
                            break;
                        default:
                            throw new InvalidDataException($"found more than one instance of stem_with_extension at depth {depth}, see: {string.Join(" | ", found.Select(x => string.Join(", ", x)))}");
                    }

                    otherStemsByPrefix.TryAdd(stem.Bytes[..depth].ToList(), otherStem);

                    break;
                case ExtPresent.Present:
                    allPaths.Add(new List<byte>(stem.Bytes[..depth]));
                    allPathsAndZs.Add((new List<byte>(stem.Bytes[..depth]), 0));
                    allPathsAndZs.Add((new List<byte>(stem.Bytes[..depth]), 1));

                    leafValuesByPathAndZ[(new List<byte>(stem.Bytes[..depth]), 0)] = FrE.One;
                    leafValuesByPathAndZ[(new List<byte>(stem.Bytes[..depth]), 1)] = FrE.FromBytesReduced(stem.Bytes.Reverse().ToArray());

                    foreach (SuffixStateDiff suffixDiff in stemStateDiff.SuffixDiffs)
                    {
                        byte suffix = suffixDiff.Suffix;
                        byte openingIndex = suffix < 128 ? (byte)2 : (byte)3;

                        allPathsAndZs.Add((new List<byte>(stem.Bytes[..depth]), openingIndex));


                        // this should definitely be the stem + openingIndex, but the path is just used for sorting
                        // and indexing the values - this is directly never used for verification
                        // so it is a good idea to used values as small as possible without the issues of collision
                        List<byte> suffixTreePath = new(stem.Bytes[..depth]) { openingIndex };

                        allPaths.Add(new List<byte>(suffixTreePath.ToArray()));
                        byte valLowerIndex = (byte)(2 * (suffix % 128));
                        byte valUpperIndex = (byte)(valLowerIndex + 1);

                        allPathsAndZs.Add((new List<byte>(suffixTreePath.ToArray()), valLowerIndex));
                        allPathsAndZs.Add((new List<byte>(suffixTreePath.ToArray()), valUpperIndex));

                        (FrE valLow, FrE valHigh) = VerkleUtils.BreakValueInLowHigh(suffixDiff.CurrentValue);

                        leafValuesByPathAndZ[(new List<byte>(suffixTreePath.ToArray()), valLowerIndex)] = valLow;
                        leafValuesByPathAndZ[(new List<byte>(suffixTreePath.ToArray()), valUpperIndex)] = valHigh;
                    }
                    break;
                case ExtPresent.None:
                    leafValuesByPathAndZ[
                        depth == 1
                            ? (new List<byte>(), stem.Bytes[depth - 1])
                            : (stem.Bytes[..depth].ToList(), stem.Bytes[depth - 1])] = FrE.Zero;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        Dictionary<List<byte>, Banderwagon> commByPath = new(new ListEqualityComparer());
        foreach ((List<byte> path, Banderwagon comm) in allPaths.Zip(commSortedByPath))
        {
            commByPath[path] = comm;
        }

        bool isTrue = VerifyVerkleProofStruct(new VerkleProofStruct(execWitness.VerkleProof.IpaProof, execWitness.VerkleProof.D),
            allPathsAndZs, leafValuesByPathAndZ, commByPath);

        updateHint = new UpdateHint
        {
            DepthAndExtByStem = depthsAndExtByStem,
            CommByPath = commByPath,
            DifferentStemNoProof = otherStemsByPrefix
        };

        return isTrue;
    }


    public static bool VerifyVerkleProof(
        IpaProofStruct ipaProof,
        Banderwagon d,
        Banderwagon[] commsSorted,
        byte[] depths,
        ExtPresent[] extensionPresent,
        Stem[] differentStemNoProof,
        List<byte[]> keys,
        List<byte[]?> values,
        Banderwagon root,
        [NotNullWhen(true)]out UpdateHint? updateHint)
    {
        updateHint = null;

        int numberOfStems = depths.Length;

        // sorted commitments including root
        List<Banderwagon> commSortedByPath = new(commsSorted.Length + 1) { root };
        commSortedByPath.AddRange(commsSorted);

        Stem[] stems = GetStemsFromKeys(CollectionsMarshal.AsSpan(keys), numberOfStems);

        // map stems to depth and extension status and create a list of stem with extension present
        Dictionary<Stem, (ExtPresent, byte)> depthsAndExtByStem = new();
        HashSet<Stem> stemsWithExtension = new();
        for (int i = 0; i < numberOfStems; i++)
        {
            ExtPresent extPresent = extensionPresent[i];
            depthsAndExtByStem.Add(stems[i], (extPresent, depths[i]));
            if (extPresent == ExtPresent.Present) stemsWithExtension.Add(stems[i]);
        }

        SortedSet<List<byte>> allPaths = new(new ListComparer());
        SortedSet<(List<byte>, byte)> allPathsAndZs = new(new ListWithByteComparer());
        Dictionary<(List<byte>, byte), FrE> leafValuesByPathAndZ = new(new ListWithByteEqualityComparer());
        SortedDictionary<List<byte>, Stem> otherStemsByPrefix = new(new ListComparer());
        foreach ((byte[] key, byte[]? value) in keys.Zip(values))
        {
            byte[] stem = key[..31];
            (ExtPresent extPres, byte depth) = depthsAndExtByStem[stem];

            for (int i = 0; i < depth; i++)
            {
                allPaths.Add(new List<byte>(stem[..i]));
                allPathsAndZs.Add((new List<byte>(stem[..i]), stem[i]));
            }

            switch (extPres)
            {
                case ExtPresent.DifferentStem:

                    allPaths.Add(new List<byte>(stem[..depth]));
                    allPathsAndZs.Add((new List<byte>(stem[..depth]), 0));
                    allPathsAndZs.Add((new List<byte>(stem[..depth]), 1));

                    // since the stem was different - value should not have been set
                    if (value != null) return false;

                    Debug.Assert(depth != stem.Length);

                    Stem otherStem;

                    // find the stems that are equal to the stem we are assuming to be without extension
                    // this happens when we initially added this stem when we were searching for another one
                    // but then in a future key, we found that we needed this stem too.
                    Stem[] found = stemsWithExtension.Where(x => x.BytesAsSpan[..depth].SequenceEqual(stem[..depth])).ToArray();

                    switch (found.Length)
                    {
                        case 0:
                            found = differentStemNoProof.Where(x => x.BytesAsSpan[..depth].SequenceEqual(stem[..depth])).ToArray();
                            Stem encounteredStem = found[^1];
                            otherStem = encounteredStem;

                            // Add extension node to proof in particular, we only want to open at (1, stem)
                            leafValuesByPathAndZ[(new List<byte>(stem[..depth]), 0)] = FrE.One;
                            leafValuesByPathAndZ.Add((new List<byte>(stem[..depth]), 1), FrE.FromBytesReduced(encounteredStem.Bytes.Reverse().ToArray()));
                            break;
                        case 1:
                            otherStem = found[0];
                            break;
                        default:
                            throw new InvalidDataException($"found more than one instance of stem_with_extension at depth {depth}, see: {string.Join(" | ", found.Select(x => string.Join(", ", x)))}");
                    }

                    otherStemsByPrefix.Add(stem[..depth].ToList(), otherStem);
                    break;
                case ExtPresent.Present:
                    allPaths.Add(new List<byte>(stem[..depth]));
                    allPathsAndZs.Add((new List<byte>(stem[..depth]), 0));
                    allPathsAndZs.Add((new List<byte>(stem[..depth]), 1));

                    leafValuesByPathAndZ[(new List<byte>(stem[..depth]), 0)] = FrE.One;
                    leafValuesByPathAndZ[(new List<byte>(stem[..depth]), 1)] = FrE.FromBytesReduced(stem.Reverse().ToArray());

                    byte suffix = key[31];
                    byte openingIndex = suffix < 128 ? (byte)2 : (byte)3;

                    allPathsAndZs.Add((new List<byte>(stem[..depth]), openingIndex));


                    // this should definitely be the stem + openingIndex, but the path is just used for sorting
                    // and indexing the values - this is directly never used for verification
                    // so it is a good idea to used values as small as possible without the issues of collision
                    List<byte> suffixTreePath = new(stem[..depth]) { openingIndex };

                    allPaths.Add(new List<byte>(suffixTreePath.ToArray()));
                    byte valLowerIndex = (byte)(2 * (suffix % 128));
                    byte valUpperIndex = (byte)(valLowerIndex + 1);

                    allPathsAndZs.Add((new List<byte>(suffixTreePath.ToArray()), valLowerIndex));
                    allPathsAndZs.Add((new List<byte>(suffixTreePath.ToArray()), valUpperIndex));

                    (FrE valLow, FrE valHigh) = VerkleUtils.BreakValueInLowHigh(value);

                    leafValuesByPathAndZ[(new List<byte>(suffixTreePath.ToArray()), valLowerIndex)] = valLow;
                    leafValuesByPathAndZ[(new List<byte>(suffixTreePath.ToArray()), valUpperIndex)] = valHigh;
                    break;
                case ExtPresent.None:
                    // If the extension was not present, then the value should be None
                    if (value != null) return false;

                    leafValuesByPathAndZ[depth == 1 ? (new List<byte>(), stem[depth - 1]) : (stem[..depth].ToList(), stem[depth - 1])] = FrE.Zero;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        Dictionary<List<byte>, Banderwagon> commByPath = new(new ListEqualityComparer());
        foreach ((List<byte> path, Banderwagon comm) in allPaths.Zip(commSortedByPath))
        {
            commByPath[path] = comm;
        }

        bool isTrue = VerifyVerkleProofStruct(new VerkleProofStruct(ipaProof, d), allPathsAndZs, leafValuesByPathAndZ, commByPath);
        updateHint = new UpdateHint
        {
            DepthAndExtByStem = depthsAndExtByStem,
            CommByPath = commByPath,
            DifferentStemNoProof = otherStemsByPrefix
        };

        return isTrue;
    }

    public static bool VerifyVerkleProof(VerkleProof proof, List<byte[]> keys, List<byte[]?> values, Banderwagon root, [NotNullWhen(true)]out UpdateHint? updateHint)
    {
        return VerifyVerkleProof(proof.Proof.IpaProof, proof.Proof.D, proof.CommsSorted, proof.VerifyHint.Depths,
            proof.VerifyHint.ExtensionPresent, proof.VerifyHint.DifferentStemNoProof.Select(x => new Stem(x)).ToArray(),
            keys, values, root,
            out updateHint);
    }

    private static bool VerifyVerkleProofStruct(VerkleProofStruct proof, SortedSet<(List<byte>, byte)> allPathsAndZs, Dictionary<(List<byte>, byte), FrE> leafValuesByPathAndZ,  Dictionary<List<byte>, Banderwagon> commByPath)
    {
        Banderwagon[] comms = new Banderwagon[allPathsAndZs.Count];
        int index = 0;
        foreach ((List<byte> path, byte z) in allPathsAndZs)
        {
            comms[index++] = commByPath[path];
        }

        SortedDictionary<(List<byte>, byte), FrE> ysByPathAndZ = new(new ListWithByteComparer());
        foreach ((List<byte> path, byte z) in allPathsAndZs)
        {
            List<byte> childPath = new(path.ToArray()) { z };

            if (!leafValuesByPathAndZ.TryGetValue((path, z), out FrE y))
            {
                y = !commByPath.TryGetValue(childPath, out Banderwagon yPoint) ? FrE.Zero : yPoint.MapToScalarField();
            }
            ysByPathAndZ.Add((new List<byte>(path.ToArray()), z), y);
        }

        IEnumerable<byte> zs = allPathsAndZs.Select(elem => elem.Item2);
        SortedDictionary<(List<byte>, byte), FrE>.ValueCollection ys = ysByPathAndZ.Values;

        List<VerkleVerifierQuery> queries = new(comms.Length);

        foreach (((FrE y, byte z), Banderwagon comm) in ys.Zip(zs).Zip(comms))
        {
            VerkleVerifierQuery query = new(comm, z, y);
            queries.Add(query);
        }

        Transcript proverTranscript = new("vt");
        MultiProof proofVerifier = new(CRS.Instance, PreComputedWeights.Instance);

        return proofVerifier.CheckMultiProof(proverTranscript, queries.ToArray(), proof);
    }
}
