// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.TreeNodes;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree
{
    private void InsertStemBatchStateless(in Stem stem, List<SuffixStateDiff> leafIndexValueMap)
    {
        Span<byte> key = new byte[32];
        stem.Bytes.CopyTo(key);
        foreach (SuffixStateDiff leaf in leafIndexValueMap)
        {
            key[31] = leaf.Suffix;
            SetLeafCache(new Hash256(key.ToArray()), leaf.CurrentValue);
        }
    }

    private void InsertBranchNodeForSync(in ReadOnlySpan<byte> path, Commitment commitment)
    {
        InternalNode node = VerkleNodes.CreateStatelessBranchNode(commitment);
        SetInternalNode(path, node);
    }

    private void InsertSubTreesForSync(in ReadOnlySpan<PathWithSubTree> subTrees)
    {
        Span<byte> key = new byte[32];
        foreach (PathWithSubTree subTree in subTrees)
        {
            subTree.Path.Bytes.CopyTo(key);
            LeafUpdateDelta leafUpdateDelta = new();
            foreach (LeafInSubTree leafs in subTree.SubTree)
            {
                key[31] = leafs.SuffixByte;
                leafUpdateDelta.UpdateDelta(GetLeafDelta(leafs.Leaf, leafs.SuffixByte), leafs.SuffixByte);
                SetLeafCache(new Hash256(key.ToArray()), leafs.Leaf);
            }

            _leafUpdateCache[subTree.Path.Bytes] = leafUpdateDelta;
        }
    }

    private bool VerifyCommitmentThenInsertStem(in ReadOnlySpan<byte> pathOfStem, byte[] stem, Commitment expectedCommitment)
    {
        InternalNode stemNode = VerkleNodes.CreateStatelessStemNode(stem);
        stemNode.UpdateCommitment(_leafUpdateCache[stem]);
        stemNode.IsStateless = false;
        if (stemNode.InternalCommitment.Point != expectedCommitment.Point)
        {
            _logger.Info($"stem commitment is also wrong: {stemNode.InternalCommitment.Point.ToBytes().ToHexString()} {expectedCommitment.Point.ToBytes().ToHexString()}");
            return false;
        }
        SetInternalNode(pathOfStem, stemNode);
        return true;
    }

    private void InsertPlaceholderForNotPresentStem(in ReadOnlySpan<byte> stem, in ReadOnlySpan<byte> pathOfStem, Commitment stemCommitment)
    {
        InternalNode stemNode = VerkleNodes.CreateStatelessStemNode(stem.ToArray(), stemCommitment);
        SetInternalNode(pathOfStem, stemNode);
    }

    private void InsertStemBatchForSync(Dictionary<byte[], List<byte[]>> stemBatch,
        IDictionary<byte[], Banderwagon> commByPath)
    {
        foreach (KeyValuePair<byte[], List<byte[]>> prefixWithStem in stemBatch)
        {
            foreach (var stem in prefixWithStem.Value)
            {
                TraverseContext context = new(stem, _leafUpdateCache[stem])
                {
                    CurrentIndex = prefixWithStem.Key.Length - 1,
                    ForSync = true
                };
                TraverseBranch(context);
            }

            commByPath[prefixWithStem.Key] = GetInternalNode(prefixWithStem.Key)!
                .InternalCommitment.Point;
        }
    }

    public bool InsertIntoStatelessTree(VerkleProof proof, List<byte[]> keys, List<byte[]?> values, Banderwagon root)
    {
        var verification = VerifyVerkleProof(proof, keys, values, root, out UpdateHint? updateHint);
        if (!verification) return false;
        InsertAfterVerification(updateHint!.Value, keys, values, root, false);
        return true;
    }

    public void InsertAfterVerification(UpdateHint hint, List<byte[]> keys, List<byte[]?> values, Banderwagon root,
        bool skipRoot = true)
    {
        if (!skipRoot)
        {
            InternalNode rootNode = new(VerkleNodeType.BranchNode, new Commitment(root));
            SetInternalNode(Array.Empty<byte>(), rootNode);
        }

        AddStatelessInternalNodes(hint);

        for (var i = 0; i < keys.Count; i++)
        {
            var value = values[i];
            if (value is null) continue;
            SetLeafCache(new Hash256(keys[i]), value);
        }
    }

    public bool InsertIntoStatelessTree(ExecutionWitness? execWitness, Banderwagon root, bool skipRoot = false)
    {
        // when witness or proof is null that means there is no access values and just save the root
        if (execWitness?.VerkleProof is null)
        {
            if (!skipRoot)
            {
                InternalNode rootNode = new(VerkleNodeType.BranchNode, new Commitment(root));
                SetInternalNode(Array.Empty<byte>(), rootNode);
            }

            return true;
        }

        var isVerified = VerifyVerkleProof(execWitness, root, out UpdateHint? updateHint);
        if (!isVerified) return false;

        if (!skipRoot)
        {
            InternalNode rootNode = new(VerkleNodeType.BranchNode, new Commitment(root));
            SetInternalNode(Array.Empty<byte>(), rootNode);
        }

        AddStatelessInternalNodes(updateHint.Value);

        foreach (StemStateDiff stemStateDiff in execWitness.StateDiff)
            InsertStemBatchStateless(stemStateDiff.Stem, stemStateDiff.SuffixDiffs);

        // TODO: is it okay that we dont support commit for stateless execution
        // CommitTree(0);
        return true;
    }

    private void AddStatelessInternalNodes(UpdateHint hint)
    {
        List<byte> pathList = [];
        foreach ((Stem stem, (ExtPresent extStatus, var depth)) in hint.DepthAndExtByStem)
        {
            pathList.Clear();
            for (var i = 0; i < depth - 1; i++)
            {
                pathList.Add(stem.Bytes[i]);
                InternalNode node = VerkleNodes.CreateStatelessBranchNode(new Commitment(hint.CommByPath[pathList.AsSpan()]));
                SetInternalNode(pathList.AsSpan(), node);
            }

            pathList.Add(stem.Bytes[depth - 1]);

            InternalNode stemNode;
            Span<byte> pathOfStem;
            switch (extStatus)
            {
                case ExtPresent.None:
                    stemNode = VerkleNodes.CreateStatelessStemNode(stem, new Commitment(), new Commitment(),
                        new Commitment(), true);
                    pathOfStem = pathList.ToArray();
                    break;
                case ExtPresent.DifferentStem:
                    Stem otherStem = hint.DifferentStemNoProof[pathList.ToArray()];
                    Commitment otherInternalCommitment = new(hint.CommByPath[pathList.AsSpan()]);
                    stemNode = VerkleNodes.CreateStatelessStemNode(otherStem, otherInternalCommitment);
                    pathOfStem = pathList.ToArray();
                    break;
                case ExtPresent.Present:
                    Commitment internalCommitment = new(hint.CommByPath[pathList.AsSpan()]);
                    Commitment? c1 = null;
                    Commitment? c2 = null;

                    pathList.Add(2);
                    if (hint.CommByPath.TryGetValue(pathList.AsSpan(), out Banderwagon c1B)) c1 = new Commitment(c1B);
                    pathList[^1] = 3;
                    if (hint.CommByPath.TryGetValue(pathList.AsSpan(), out Banderwagon c2B)) c2 = new Commitment(c2B);

                    stemNode = VerkleNodes.CreateStatelessStemNode(stem, c1, c2, internalCommitment, false);
                    pathOfStem = new byte[pathList.Count - 1];
                    pathList.AsSpan()[..(pathList.Count - 1)].CopyTo(pathOfStem);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            SetInternalNode(pathOfStem, stemNode, extStatus != ExtPresent.DifferentStem);
        }
    }

    public bool CreateStatelessTreeFromRange(VerkleProof proof, Banderwagon rootPoint, Stem startStem, Stem endStem,
        in ReadOnlySpan<PathWithSubTree> subTrees)
    {
        var numberOfStems = 2;
        if (subTrees.Length == 0 || (subTrees.Length == 1 && endStem == startStem)) numberOfStems = 1;
        Stem[] stems = [startStem, endStem];
        if (numberOfStems == 1) stems = [startStem];

        // create a array of sorted commitments including root commitment
        var commSortedByPath = new Banderwagon[proof.CommsSorted.Length + 1];
        commSortedByPath[0] = rootPoint;
        proof.CommsSorted.CopyTo(commSortedByPath.AsSpan(1));

        // map stems to depth and extension status and create a list of stem with extension present
        Dictionary<byte[], (ExtPresent, byte)> depthsAndExtByStem = new(Bytes.EqualityComparer);
        HashSet<byte[]> stemsWithExtension = new(Bytes.EqualityComparer);
        for (var i = 0; i < numberOfStems; i++)
        {
            ExtPresent extPresent = proof.VerifyHint.ExtensionPresent[i];
            depthsAndExtByStem.Add(stems[i].Bytes, (extPresent, proof.VerifyHint.Depths[i]));
            if (extPresent == ExtPresent.Present) stemsWithExtension.Add(stems[i].Bytes);
        }

        SortedSet<byte[]> allPaths = new(new ByteListComparer());
        SortedSet<(byte[], byte)> allPathsAndZs = new(new ListWithByteComparer());
        Dictionary<(byte[], byte), FrE> leafValuesByPathAndZ = new(new ListWithByteEqualityComparer());
        SortedDictionary<byte[], byte[]> otherStemsByPrefix = new(new ByteListComparer());

        var prefixLength = 0;
        while (prefixLength < Stem.Size)
        {
            if (startStem.Bytes[prefixLength] != endStem.Bytes[prefixLength]) break;
            prefixLength++;
        }

        var keyIndex = 0;
        foreach (Stem stem in stems)
        {
            (ExtPresent extPres, var depth) = depthsAndExtByStem[stem.Bytes];

            for (var i = 0; i < depth; i++)
            {
                allPaths.Add(stem.Bytes[..i]);
                if (i < prefixLength)
                {
                    allPathsAndZs.Add((stem.Bytes[..i], stem.Bytes[i]));
                    continue;
                }

                int startIndex = startStem.Bytes[i];
                int endIndex = endStem.Bytes[i];
                if (i > prefixLength)
                {
                    if (keyIndex == 0) endIndex = 255;
                    else startIndex = 0;
                }

                for (var j = startIndex; j <= endIndex; j++)
                    allPathsAndZs.Add((stem.Bytes[..i], (byte)j));
            }

            switch (extPres)
            {
                case ExtPresent.DifferentStem:

                    allPaths.Add(stem.Bytes[..depth]);
                    allPathsAndZs.Add((stem.Bytes[..depth], 0));
                    allPathsAndZs.Add((stem.Bytes[..depth], 1));

                    byte[] otherStem;

                    // find the stems that are equal to the stem we are assuming to be without extension
                    // this happens when we initially added this stem when we were searching for another one
                    // but then in a future key, we found that we needed this stem too.
                    var found = stemsWithExtension.Where(x => x[..depth].SequenceEqual(stem.Bytes[..depth])).ToArray();

                    switch (found.Length)
                    {
                        case 0:
                            found = proof.VerifyHint.DifferentStemNoProof
                                .Where(x => x[..depth].SequenceEqual(stem.Bytes[..depth])).ToArray();
                            var encounteredStem = found[^1];
                            otherStem = encounteredStem;

                            // Add extension node to proof in particular, we only want to open at (1, stem)
                            leafValuesByPathAndZ[(stem.Bytes[..depth], 0)] = FrE.One;
                            leafValuesByPathAndZ.Add((stem.Bytes[..depth], 1),
                                FrE.FromBytesReduced(encounteredStem.Reverse().ToArray()));
                            break;
                        case 1:
                            otherStem = found[0];
                            break;
                        default:
                            throw new InvalidDataException(
                                $"found more than one instance of stem_with_extension at depth {depth}, see: {string.Join(" | ", found.Select(x => string.Join(", ", x)))}");
                    }

                    otherStemsByPrefix.Add(stem.Bytes[..depth], otherStem);
                    break;
                case ExtPresent.Present:
                    allPaths.Add(stem.Bytes[..depth]);
                    allPathsAndZs.Add((stem.Bytes[..depth], 0));
                    allPathsAndZs.Add((stem.Bytes[..depth], 1));

                    leafValuesByPathAndZ[(stem.Bytes[..depth], 0)] = FrE.One;
                    leafValuesByPathAndZ[(stem.Bytes[..depth], 1)] =
                        FrE.FromBytesReduced(stem.Bytes.Reverse().ToArray());
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

            keyIndex++;
        }

        SpanDictionary<byte, Banderwagon> commByPath = new(Bytes.SpanEqualityComparer);
        foreach ((byte[] path, Banderwagon comm) in allPaths.Zip(commSortedByPath)) commByPath[path] = comm;

        HashSet<byte[]> subTreesToCreate = UpdatePathsAndReturnSubTreesToCreate(allPaths, allPathsAndZs, subTrees,
            startStem.BytesAsSpan, endStem.BytesAsSpan, otherStemsByPrefix);
        InsertSubTreesForSync(subTrees);

        List<byte> pathList = new();
        InsertBranchNodeForSync(CollectionsMarshal.AsSpan(pathList), new Commitment(commByPath[pathList.ToArray()]));
        foreach ((var stem, (ExtPresent extStatus, var depth)) in depthsAndExtByStem)
        {
            pathList.Clear();
            for (var i = 0; i < depth - 1; i++)
            {
                pathList.Add(stem[i]);
                InsertBranchNodeForSync(CollectionsMarshal.AsSpan(pathList), new Commitment(commByPath[pathList.ToArray()]));
            }

            pathList.Add(stem[depth - 1]);

            switch (extStatus)
            {
                case ExtPresent.None:
                    InsertPlaceholderForNotPresentStem(stem, CollectionsMarshal.AsSpan(pathList), new Commitment());
                    break;
                case ExtPresent.DifferentStem:
                    var otherStem = otherStemsByPrefix[pathList.ToArray()];
                    InsertPlaceholderForNotPresentStem(otherStem, CollectionsMarshal.AsSpan(pathList),
                        new Commitment(commByPath[pathList.ToArray()]));
                    break;
                case ExtPresent.Present:
                    Commitment internalCommitment = new(commByPath[pathList.ToArray()]);
                    if (!VerifyCommitmentThenInsertStem(CollectionsMarshal.AsSpan(pathList), stem, internalCommitment))
                        return false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        List<byte[]> allStemsWithoutStartAndEndStems = new(subTrees.Length);
        foreach (PathWithSubTree x in subTrees)
        {
            if (!x.Path.Bytes.SequenceEqual(startStem.Bytes) && !x.Path.Bytes.SequenceEqual(endStem.Bytes))
            {
                allStemsWithoutStartAndEndStems.Add(x.Path.Bytes);
            }
        }

        var stemIndex = 0;
        Dictionary<byte[], List<byte[]>> stemBatch = new(Bytes.EqualityComparer);
        foreach (var stemPrefix in subTreesToCreate)
        {
            stemBatch.Add(stemPrefix, new List<byte[]>());
            while (stemIndex < allStemsWithoutStartAndEndStems.Count)
                if (Bytes.EqualityComparer.Equals(stemPrefix,
                        allStemsWithoutStartAndEndStems[stemIndex][..stemPrefix.Length]))
                {
                    stemBatch[stemPrefix].Add(allStemsWithoutStartAndEndStems[stemIndex]);
                    stemIndex++;
                }
                else
                {
                    break;
                }
        }

        InsertStemBatchForSync(stemBatch, commByPath);
        var verification = VerifyVerkleProofStruct(proof.Proof, allPathsAndZs, leafValuesByPathAndZ, commByPath);
        if (!verification) Reset();
        // else CommitTree(0);

        return verification;
    }

    private static HashSet<byte[]> UpdatePathsAndReturnSubTreesToCreate(
        SortedSet<byte[]> allPaths,
        SortedSet<(byte[], byte)> allPathsAndZs,
        in ReadOnlySpan<PathWithSubTree> stems,
        in ReadOnlySpan<byte> startStem,
        in ReadOnlySpan<byte> endStem,
        SortedDictionary<byte[], byte[]> otherStemPrefix
    )
    {
        ISpanEqualityComparer<byte> comparer = Bytes.SpanEqualityComparer;
        HashSet<byte[]> subTreesToCreate = new(Bytes.EqualityComparer);
        foreach (PathWithSubTree subTree in stems)
        {
            if (comparer.Equals(subTree.Path.Bytes, startStem)) continue;
            if (comparer.Equals(subTree.Path.Bytes, endStem)) continue;
            for (var i = 0; i < 32; i++)
            {
                byte[] prefix = subTree.Path.Bytes[..i];
                if (allPaths.Contains(prefix))
                {
                    // no need to add commitments for otherStem
                    if (otherStemPrefix.TryGetValue(prefix, out _))
                    {
                        subTreesToCreate.Add(prefix.ToArray());
                        break;
                    }

                    allPathsAndZs.Add((prefix, subTree.Path.Bytes[i]));
                }
                else
                {
                    subTreesToCreate.Add(prefix.ToArray());
                    break;
                }
            }
        }

        return subTreesToCreate;
    }
}
