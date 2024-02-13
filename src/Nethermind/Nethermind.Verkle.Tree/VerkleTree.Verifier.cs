// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Polynomial;
using Nethermind.Verkle.Proofs;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree
{
    private static Stem[] GetStemsFromKeys(Span<byte[]> keys, int numberOfStems)
    {
        var stems = new Stem[numberOfStems];
        stems[0] = keys[0][..31];

        var stemIndex = 1;
        for (var i = 1; i < keys.Length; i++)
        {
            Stem currentStem = keys[i][..31];
            if (stems[stemIndex - 1].Equals(currentStem)) continue;
            stems[stemIndex++] = currentStem;
        }

        return stems;
    }

    private static Stem[] GetStemsFromStemStateDiff(StemStateDiff[] suffixDiffs)
    {
        var stems = new Stem[suffixDiffs.Length];
        for (var i = 0; i < suffixDiffs.Length; i++) stems[i] = suffixDiffs[i].Stem;
        return stems;
    }

    private static bool VerifyVerkleProof(
        ExecutionWitness execWitness,
        Banderwagon root,
        [NotNullWhen(true)] out UpdateHint? updateHint)
    {
        // var logg = SimpleConsoleLogger.Instance;
        updateHint = null;
        WitnessVerkleProof verkleProof = execWitness.VerkleProof!;
        var numberOfStems = verkleProof.DepthExtensionPresent.Length;

        // sorted commitments including root
        var commSortedByPath = new Banderwagon[verkleProof.CommitmentsByPath.Length + 1];
        commSortedByPath[0] = root;
        verkleProof.CommitmentsByPath.CopyTo(commSortedByPath, 1);

        Stem[] stems = GetStemsFromStemStateDiff(execWitness.StateDiff);

        Dictionary<Stem, (ExtPresent, byte)> depthsAndExtByStem = new();
        HashSet<Stem> stemsWithExtension = [];
        for (var i = 0; i < numberOfStems; i++)
        {
            var extAndDepth = verkleProof.DepthExtensionPresent[i];
            var depth = (byte)(extAndDepth >> 3);
            var extPresent = (ExtPresent)(extAndDepth & 3);
            depthsAndExtByStem.Add(stems[i], (extPresent, depth));
            if (extPresent == ExtPresent.Present) stemsWithExtension.Add(stems[i]);
        }

        SortedSet<byte[]> allPaths = new(new ByteListComparer());
        SortedSet<(byte[], byte)> allPathsAndZs = new(new ListWithByteComparer());
        Dictionary<(byte[], byte), FrE> leafValuesByPathAndZ = new(new ListWithByteEqualityComparer());
        SortedDictionary<byte[], Stem> otherStemsByPrefix = new(new ByteListComparer());

        foreach (StemStateDiff stemStateDiff in execWitness.StateDiff)
        {
            Stem stem = stemStateDiff.Stem;
            (ExtPresent extPres, var depth) = depthsAndExtByStem[stem];

            for (var i = 0; i < depth; i++)
            {
                allPaths.Add(stem.Bytes[..i]);
                allPathsAndZs.Add((stem.Bytes[..i], stem.Bytes[i]));
            }

            switch (extPres)
            {
                case ExtPresent.DifferentStem:
                    allPaths.Add(stem.Bytes[..depth]);
                    allPathsAndZs.Add((stem.Bytes[..depth], 0));
                    allPathsAndZs.Add((stem.Bytes[..depth], 1));

                    Stem otherStem;

                    // find the stems that are equal to the stem we are assuming to be without extension
                    // this happens when we initially added this stem when we were searching for another one
                    // but then in a future key, we found that we needed this stem too.
                    Stem[] found = stemsWithExtension
                        .Where(x => x.BytesAsSpan[..depth].SequenceEqual(stem.Bytes[..depth])).ToArray();

                    switch (found.Length)
                    {
                        case 0:
                            found = verkleProof.OtherStems
                                .Where(x => x.BytesAsSpan[..depth].SequenceEqual(stem.Bytes[..depth])).ToArray();
                            Stem encounteredStem = found[^1];
                            otherStem = encounteredStem;

                            // Add extension node to proof in particular, we only want to open at (1, stem)
                            leafValuesByPathAndZ[(stem.Bytes[..depth], 0)] = FrE.One;
                            leafValuesByPathAndZ.TryAdd((stem.Bytes[..depth], 1),
                                FrE.FromBytesReduced(encounteredStem.Bytes.Reverse().ToArray()));
                            break;
                        case 1:
                            otherStem = found[0];
                            break;
                        default:
                            throw new InvalidDataException(
                                $"found more than one instance of stem_with_extension at depth {depth}, see: {string.Join(" | ", found.Select(x => string.Join(", ", x)))}");
                    }

                    otherStemsByPrefix.TryAdd(stem.Bytes[..depth], otherStem);

                    break;
                case ExtPresent.Present:
                    allPaths.Add(stem.Bytes[..depth]);
                    allPathsAndZs.Add((stem.Bytes[..depth], 0));
                    allPathsAndZs.Add((stem.Bytes[..depth], 1));

                    leafValuesByPathAndZ[(stem.Bytes[..depth], 0)] = FrE.One;
                    leafValuesByPathAndZ[(stem.Bytes[..depth], 1)] =
                        FrE.FromBytesReduced(stem.Bytes.Reverse().ToArray());

                    foreach (SuffixStateDiff suffixDiff in stemStateDiff.SuffixDiffs)
                    {
                        var suffix = suffixDiff.Suffix;
                        var openingIndex = suffix < 128 ? (byte)2 : (byte)3;

                        allPathsAndZs.Add((stem.Bytes[..depth], openingIndex));


                        // this should definitely be the stem + openingIndex, but the path is just used for sorting
                        // and indexing the values - this is directly never used for verification
                        // so it is a good idea to used values as small as possible without the issues of collision
                        List<byte> suffixTreePath = new(stem.Bytes[..depth]) { openingIndex };

                        allPaths.Add(suffixTreePath.ToArray());
                        var valLowerIndex = (byte)(2 * (suffix % 128));
                        var valUpperIndex = (byte)(valLowerIndex + 1);

                        allPathsAndZs.Add((suffixTreePath.ToArray(), valLowerIndex));
                        allPathsAndZs.Add((suffixTreePath.ToArray(), valUpperIndex));

                        (FrE valLow, FrE valHigh) = VerkleUtils.BreakValueInLowHigh(suffixDiff.CurrentValue);

                        leafValuesByPathAndZ[(suffixTreePath.ToArray(), valLowerIndex)] = valLow;
                        leafValuesByPathAndZ[(suffixTreePath.ToArray(), valUpperIndex)] = valHigh;
                    }

                    break;
                case ExtPresent.None:
                    leafValuesByPathAndZ[
                        depth == 1
                            ? (Array.Empty<byte>(), stem.Bytes[depth - 1])
                            : (stem.Bytes[..depth], stem.Bytes[depth - 1])] = FrE.Zero;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        SpanDictionary<byte, Banderwagon> commByPath = new(Bytes.SpanEqualityComparer);
        int idx = 0;
        foreach (var path in allPaths) commByPath[path] = commSortedByPath[idx++];

        var proofStruct = new VerkleProofStruct(verkleProof.IpaProof, verkleProof.D);
        var isTrue = VerifyVerkleProofStruct(proofStruct, allPathsAndZs, leafValuesByPathAndZ, commByPath);

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
        Banderwagon[] commitmentsSorted,
        byte[] depths,
        ExtPresent[] extensionPresent,
        Stem[] differentStemNoProof,
        List<byte[]> keys,
        List<byte[]?> values,
        Banderwagon root,
        [NotNullWhen(true)] out UpdateHint? updateHint)
    {
        updateHint = null;

        var numberOfStems = depths.Length;

        // sorted commitments including root
        List<Banderwagon> commSortedByPath = new(commitmentsSorted.Length + 1) { root };
        commSortedByPath.AddRange(commitmentsSorted);

        Stem[] stems = GetStemsFromKeys(CollectionsMarshal.AsSpan(keys), numberOfStems);

        // map stems to depth and extension status and create a list of stem with extension present
        Dictionary<Stem, (ExtPresent, byte)> depthsAndExtByStem = new();
        HashSet<Stem> stemsWithExtension = new();
        for (var i = 0; i < numberOfStems; i++)
        {
            ExtPresent extPresent = extensionPresent[i];
            depthsAndExtByStem.Add(stems[i], (extPresent, depths[i]));
            if (extPresent == ExtPresent.Present) stemsWithExtension.Add(stems[i]);
        }

        SortedSet<byte[]> allPaths = new(new ByteListComparer());
        SortedSet<(byte[], byte)> allPathsAndZs = new(new ListWithByteComparer());
        Dictionary<(byte[], byte), FrE> leafValuesByPathAndZ = new(new ListWithByteEqualityComparer());
        SortedDictionary<byte[], Stem> otherStemsByPrefix = new(new ByteListComparer());

        var numOfKeyValuePair = values.Count;
        for (int x = 0; x < numOfKeyValuePair; x++)
        {
            byte[] key = keys[x];
            byte[] value = values[x];

            var stem = key[..31];
            (ExtPresent extPres, var depth) = depthsAndExtByStem[stem];

            for (var i = 0; i < depth; i++)
            {
                allPaths.Add(stem[..i]);
                allPathsAndZs.Add((stem[..i], stem[i]));
            }

            switch (extPres)
            {
                case ExtPresent.DifferentStem:

                    allPaths.Add(stem[..depth]);
                    allPathsAndZs.Add((stem[..depth], 0));
                    allPathsAndZs.Add((stem[..depth], 1));

                    // since the stem was different - value should not have been set
                    if (value != null) return false;

                    Debug.Assert(depth != stem.Length);

                    Stem otherStem;

                    // find the stems that are equal to the stem we are assuming to be without extension
                    // this happens when we initially added this stem when we were searching for another one
                    // but then in a future key, we found that we needed this stem too.
                    Stem[] found = stemsWithExtension.Where(x => x.BytesAsSpan[..depth].SequenceEqual(stem[..depth]))
                        .ToArray();

                    switch (found.Length)
                    {
                        case 0:
                            found = differentStemNoProof.Where(x => x.BytesAsSpan[..depth].SequenceEqual(stem[..depth]))
                                .ToArray();
                            Stem encounteredStem = found[^1];
                            otherStem = encounteredStem;

                            // Add extension node to proof in particular, we only want to open at (1, stem)
                            leafValuesByPathAndZ[(stem[..depth], 0)] = FrE.One;
                            leafValuesByPathAndZ.Add((stem[..depth], 1),
                                FrE.FromBytesReduced(encounteredStem.Bytes.Reverse().ToArray()));
                            break;
                        case 1:
                            otherStem = found[0];
                            break;
                        default:
                            throw new InvalidDataException(
                                $"found more than one instance of stem_with_extension at depth {depth}, see: {string.Join(" | ", found.Select(x => string.Join(", ", x)))}");
                    }

                    otherStemsByPrefix.Add(stem[..depth], otherStem);
                    break;
                case ExtPresent.Present:
                    allPaths.Add(stem[..depth]);
                    allPathsAndZs.Add((stem[..depth], 0));
                    allPathsAndZs.Add((stem[..depth], 1));

                    leafValuesByPathAndZ[(stem[..depth], 0)] = FrE.One;
                    leafValuesByPathAndZ[(stem[..depth], 1)] =
                        FrE.FromBytesReduced(stem.Reverse().ToArray());

                    var suffix = key[31];
                    var openingIndex = suffix < 128 ? (byte)2 : (byte)3;

                    allPathsAndZs.Add((stem[..depth], openingIndex));


                    // this should definitely be the stem + openingIndex, but the path is just used for sorting
                    // and indexing the values - this is directly never used for verification
                    // so it is a good idea to used values as small as possible without the issues of collision
                    List<byte> suffixTreePath = new(stem[..depth]) { openingIndex };

                    allPaths.Add(suffixTreePath.ToArray());
                    var valLowerIndex = (byte)(2 * (suffix % 128));
                    var valUpperIndex = (byte)(valLowerIndex + 1);

                    allPathsAndZs.Add((suffixTreePath.ToArray(), valLowerIndex));
                    allPathsAndZs.Add((suffixTreePath.ToArray(), valUpperIndex));

                    (FrE valLow, FrE valHigh) = VerkleUtils.BreakValueInLowHigh(value);

                    leafValuesByPathAndZ[(suffixTreePath.ToArray(), valLowerIndex)] = valLow;
                    leafValuesByPathAndZ[(suffixTreePath.ToArray(), valUpperIndex)] = valHigh;
                    break;
                case ExtPresent.None:
                    // If the extension was not present, then the value should be None
                    if (value != null) return false;

                    leafValuesByPathAndZ[
                            depth == 1
                                ? (Array.Empty<byte>(), stem[depth - 1])
                                : (stem[..depth], stem[depth - 1])] =
                        FrE.Zero;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        SpanDictionary<byte, Banderwagon> commByPath = new(Bytes.SpanEqualityComparer);
        foreach ((byte[] path, Banderwagon comm) in allPaths.Zip(commSortedByPath)) commByPath[path] = comm;

        var isTrue = VerifyVerkleProofStruct(new VerkleProofStruct(ipaProof, d), allPathsAndZs, leafValuesByPathAndZ,
            commByPath);
        updateHint = new UpdateHint
        {
            DepthAndExtByStem = depthsAndExtByStem,
            CommByPath = commByPath,
            DifferentStemNoProof = otherStemsByPrefix
        };

        return isTrue;
    }

    public static bool VerifyVerkleProof(VerkleProof proof, List<byte[]> keys, List<byte[]?> values, Banderwagon root,
        [NotNullWhen(true)] out UpdateHint? updateHint)
    {
        return VerifyVerkleProof(proof.Proof.IpaProof, proof.Proof.D, proof.CommsSorted, proof.VerifyHint.Depths,
            proof.VerifyHint.ExtensionPresent, proof.VerifyHint.DifferentStemNoProof.Select(x => new Stem(x)).ToArray(),
            keys, values, root,
            out updateHint);
    }

    private static bool VerifyVerkleProofStruct(VerkleProofStruct proof, SortedSet<(byte[], byte)> allPathsAndZs,
        Dictionary<(byte[], byte), FrE> leafValuesByPathAndZ, SpanDictionary<byte, Banderwagon> commByPath)
    {
        var comms = new Banderwagon[allPathsAndZs.Count];
        var index = 0;
        foreach ((byte[] path, var z) in allPathsAndZs) comms[index++] = commByPath[path];

        SortedDictionary<(byte[], byte), FrE> ysByPathAndZ = new(new ListWithByteComparer());
        foreach ((byte[] path, var z) in allPathsAndZs)
        {
            byte[] childPath = [.. path.ToArray(), z];

            if (!leafValuesByPathAndZ.TryGetValue((path, z), out FrE y))
                y = !commByPath.TryGetValue(childPath, out Banderwagon yPoint) ? FrE.Zero : yPoint.MapToScalarField();
            ysByPathAndZ.Add((path.ToArray(), z), y);
        }

        IEnumerable<byte> zs = allPathsAndZs.Select(elem => elem.Item2);
        SortedDictionary<(byte[], byte), FrE>.ValueCollection ys = ysByPathAndZ.Values;

        List<VerkleVerifierQuery> queries = new(comms.Length);

        foreach (((FrE y, var z), Banderwagon comm) in ys.Zip(zs).Zip(comms))
        {
            VerkleVerifierQuery query = new(comm, z, y);
            queries.Add(query);
        }
        // Console.WriteLine("Verifier Query");
        // foreach (VerkleVerifierQuery query in queries)
        // {
        //     Console.WriteLine($"{query.NodeCommitPoint.ToBytes().ToHexString()}:{query.ChildIndex}:{query.ChildHash.ToBytes().ToHexString()}");
        // }

        Transcript proverTranscript = new("vt");
        MultiProof proofVerifier = new(CRS.Instance, PreComputedWeights.Instance);

        return proofVerifier.CheckMultiProof(proverTranscript, queries.ToArray(), proof);
    }
}
