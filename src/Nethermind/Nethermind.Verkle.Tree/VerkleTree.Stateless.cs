// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.Proofs;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree
{

    private void InsertStemBatchStateless(in Stem stem, IEnumerable<LeafInSubTree> leafIndexValueMap)
    {
        InsertStemBatchStateless(stem.BytesAsSpan, leafIndexValueMap);
    }

    private void InsertStemBatchStateless(ReadOnlySpan<byte> stem, IEnumerable<LeafInSubTree> leafIndexValueMap)
    {
        Span<byte> key = new byte[32];
        stem.CopyTo(key);
        foreach (LeafInSubTree leaf in leafIndexValueMap)
        {
            key[31] = leaf.SuffixByte;
            SetLeafCache(key.ToArray(), leaf.Leaf);
        }
    }

    private void InsertBranchNodeForSync(byte[] path, Commitment commitment)
    {
        InternalNode node = VerkleNodes.CreateStatelessBranchNode(commitment);
        SetInternalNode(path, node);
    }

    private void InsertSubTreesForSync(PathWithSubTree[] subTrees)
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
                SetLeafCache(key.ToArray(), leafs.Leaf);
            }
            _leafUpdateCache[subTree.Path.Bytes] = leafUpdateDelta;
        }
    }

    private bool VerifyCommitmentThenInsertStem(byte[] pathOfStem, byte[] stem, Commitment expectedCommitment)
    {
        InternalNode stemNode = VerkleNodes.CreateStatelessStemNode(stem);
        stemNode.UpdateCommitment(_leafUpdateCache[stem]);
        if (stemNode.InternalCommitment.Point != expectedCommitment.Point) return false;
        SetInternalNode(pathOfStem, stemNode);
        return true;
    }

    private void InsertPlaceholderForNotPresentStem(Span<byte> stem, byte[] pathOfStem, Commitment stemCommitment)
    {
        InternalNode stemNode = VerkleNodes.CreateStatelessStemNode(stem.ToArray(), stemCommitment);
        SetInternalNode(pathOfStem, stemNode);
    }

    private void InsertStemBatchForSync(Dictionary<byte[], List<byte[]>> stemBatch,
        IDictionary<List<byte>, Banderwagon> commByPath)
    {
        foreach (KeyValuePair<byte[], List<byte[]>> prefixWithStem in stemBatch)
        {
            foreach (byte[] stem in prefixWithStem.Value)
            {
                TraverseContext context = new(stem, _leafUpdateCache[stem])
                {
                    CurrentIndex = prefixWithStem.Key.Length - 1,
                    ForSync = true
                };
                TraverseBranch(context);
            }

            commByPath[new List<byte>(prefixWithStem.Key)] = GetInternalNode(prefixWithStem.Key)!
                .InternalCommitment.Point;
        }
    }
    public bool InsertIntoStatelessTree(VerkleProof proof, List<byte[]> keys, List<byte[]?> values, Banderwagon root)
    {
        bool verification = VerifyVerkleProof(proof, keys, values, root, out UpdateHint? updateHint);
        if (!verification) return false;
        InsertAfterVerification(updateHint!.Value, keys, values, root, false);
        return true;
    }

    public void InsertAfterVerification(UpdateHint hint, List<byte[]> keys, List<byte[]?> values, Banderwagon root, bool skipRoot = true)
    {
        if (!skipRoot)
        {
            InternalNode rootNode = new(VerkleNodeType.BranchNode, new Commitment(root));
            SetInternalNode(Array.Empty<byte>(), rootNode);
        }

        AddStatelessInternalNodes(hint);

        for (int i = 0; i < keys.Count; i++)
        {
            byte[]? value = values[i];
            if(value is null) continue;
            SetLeafCache(keys[i], value);
        }
    }

    public bool InsertIntoStatelessTree(ExecutionWitness? execWitness, Banderwagon root, bool skipRoot = false)
    {
        if (execWitness is null || execWitness.VerkleProof is null)
        {
            if (!skipRoot)
            {
                InternalNode rootNode = new(VerkleNodeType.BranchNode, new Commitment(root));
                SetInternalNode(Array.Empty<byte>(), rootNode);
            }

            return true;
        }

        bool isVerified = VerifyVerkleProof(execWitness, root, out UpdateHint? updateHint);
        if (!isVerified) return false;

        if (!skipRoot)
        {
            InternalNode rootNode = new(VerkleNodeType.BranchNode, new Commitment(root));
            SetInternalNode(Array.Empty<byte>(), rootNode);
        }

        AddStatelessInternalNodes(updateHint.Value);

        foreach (StemStateDiff stemStateDiff in execWitness.StateDiff)
        {
            InsertStemBatchStateless(stemStateDiff.Stem,
                stemStateDiff.SuffixDiffs.Select(x => new LeafInSubTree(x.Suffix, x.CurrentValue)));
        }

        CommitTree(0);
        return true;

    }

    private void AddStatelessInternalNodes(UpdateHint hint)
    {
        List<byte> pathList = new();
        foreach ((Stem stem, (ExtPresent extStatus, byte depth)) in hint.DepthAndExtByStem)
        {
            pathList.Clear();
            for (int i = 0; i < depth - 1; i++)
            {
                pathList.Add(stem.Bytes[i]);
                InternalNode node = VerkleNodes.CreateStatelessBranchNode(new Commitment(hint.CommByPath[pathList]));
                SetInternalNode(pathList.ToArray(), node);
            }

            pathList.Add(stem.Bytes[depth-1]);

            InternalNode stemNode;
            byte[] pathOfStem;
            switch (extStatus)
            {
                case ExtPresent.None:
                    stemNode = VerkleNodes.CreateStatelessStemNode(stem, new Commitment(), new Commitment(), new Commitment());
                    pathOfStem = pathList.ToArray();
                    break;
                case ExtPresent.DifferentStem:
                    Stem otherStem = hint.DifferentStemNoProof[pathList];
                    Commitment otherInternalCommitment = new(hint.CommByPath[pathList]);
                    stemNode = VerkleNodes.CreateStatelessStemNode(otherStem, otherInternalCommitment);
                    pathOfStem = pathList.ToArray();
                    break;
                case ExtPresent.Present:
                    Commitment internalCommitment = new(hint.CommByPath[pathList]);
                    Commitment? c1 = null;
                    Commitment? c2 = null;

                    pathList.Add(2);
                    if (hint.CommByPath.TryGetValue(pathList, out Banderwagon c1B)) c1 = new Commitment(c1B);
                    pathList[^1] = 3;
                    if (hint.CommByPath.TryGetValue(pathList, out Banderwagon c2B)) c2 = new Commitment(c2B);

                    stemNode = VerkleNodes.CreateStatelessStemNode(stem, c1, c2, internalCommitment);
                    pathOfStem = new byte[pathList.Count - 1];
                    pathList.CopyTo(0, pathOfStem, 0, pathList.Count - 1);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            SetInternalNode(pathOfStem, stemNode, extStatus != ExtPresent.DifferentStem);
        }
    }

    public bool CreateStatelessTreeFromRange(VerkleProof proof, Banderwagon rootPoint, Stem startStem, Stem endStem, PathWithSubTree[] subTrees)
    {
        const int numberOfStems = 2;
        List<Banderwagon> commSortedByPath = new(proof.CommsSorted.Length + 1) { rootPoint };
        commSortedByPath.AddRange(proof.CommsSorted);

        Stem[] stems = { startStem, endStem };

        // map stems to depth and extension status and create a list of stem with extension present
        Dictionary<byte[], (ExtPresent, byte)> depthsAndExtByStem = new(Bytes.EqualityComparer);
        HashSet<byte[]> stemsWithExtension = new(Bytes.EqualityComparer);
        for (int i = 0; i < numberOfStems; i++)
        {
            ExtPresent extPresent = proof.VerifyHint.ExtensionPresent[i];
            depthsAndExtByStem.Add(stems[i].Bytes, (extPresent, proof.VerifyHint.Depths[i]));
            if (extPresent == ExtPresent.Present) stemsWithExtension.Add(stems[i].Bytes);
        }

        SortedSet<List<byte>> allPaths = new(new ListComparer());
        SortedSet<(List<byte>, byte)> allPathsAndZs = new(new ListWithByteComparer());
        Dictionary<(List<byte>, byte), FrE> leafValuesByPathAndZ = new(new ListWithByteEqualityComparer());
        SortedDictionary<List<byte>, byte[]> otherStemsByPrefix = new(new ListComparer());

        int prefixLength = 0;
        while (prefixLength < Stem.Size)
        {
            if (startStem.Bytes[prefixLength] != endStem.Bytes[prefixLength]) break;
            prefixLength++;
        }

        int keyIndex = 0;
        foreach (Stem stem in stems)
        {
            (ExtPresent extPres, byte depth) = depthsAndExtByStem[stem.Bytes];

            for (int i = 0; i < depth; i++)
            {
                allPaths.Add(new List<byte>(stem.Bytes[..i]));
                if (i < prefixLength)
                {
                    allPathsAndZs.Add((new List<byte>(stem.Bytes[..i]), stem.Bytes[i]));
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
                    allPathsAndZs.Add((new List<byte>(stem.Bytes[..i]), (byte)j));
                }
            }

            switch (extPres)
            {
                case ExtPresent.DifferentStem:

                    allPaths.Add(new List<byte>(stem.Bytes[..depth]));
                    allPathsAndZs.Add((new List<byte>(stem.Bytes[..depth]), 0));
                    allPathsAndZs.Add((new List<byte>(stem.Bytes[..depth]), 1));

                    byte[] otherStem;

                    // find the stems that are equal to the stem we are assuming to be without extension
                    // this happens when we initially added this stem when we were searching for another one
                    // but then in a future key, we found that we needed this stem too.
                    byte[][] found = stemsWithExtension.Where(x => x[..depth].SequenceEqual(stem.Bytes[..depth])).ToArray();

                    switch (found.Length)
                    {
                        case 0:
                            found = proof.VerifyHint.DifferentStemNoProof.Where(x => x[..depth].SequenceEqual(stem.Bytes[..depth])).ToArray();
                            byte[] encounteredStem = found[^1];
                            otherStem = encounteredStem;

                            // Add extension node to proof in particular, we only want to open at (1, stem)
                            leafValuesByPathAndZ[(new List<byte>(stem.Bytes[..depth]), 0)] = FrE.One;
                            leafValuesByPathAndZ.Add((new List<byte>(stem.Bytes[..depth]), 1), FrE.FromBytesReduced(encounteredStem.Reverse().ToArray()));
                            break;
                        case 1:
                            otherStem = found[0];
                            break;
                        default:
                            throw new InvalidDataException($"found more than one instance of stem_with_extension at depth {depth}, see: {string.Join(" | ", found.Select(x => string.Join(", ", x)))}");
                    }

                    otherStemsByPrefix.Add(stem.Bytes[..depth].ToList(), otherStem);
                    break;
                case ExtPresent.Present:
                    allPaths.Add(new List<byte>(stem.Bytes[..depth]));
                    allPathsAndZs.Add((new List<byte>(stem.Bytes[..depth]), 0));
                    allPathsAndZs.Add((new List<byte>(stem.Bytes[..depth]), 1));

                    leafValuesByPathAndZ[(new List<byte>(stem.Bytes[..depth]), 0)] = FrE.One;
                    leafValuesByPathAndZ[(new List<byte>(stem.Bytes[..depth]), 1)] = FrE.FromBytesReduced(stem.Bytes.Reverse().ToArray());
                    break;
                case ExtPresent.None:
                    leafValuesByPathAndZ[depth == 1 ? (new List<byte>(), stem.Bytes[depth - 1]) : (stem.Bytes[..depth].ToList(), stem.Bytes[depth - 1])] = FrE.Zero;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            keyIndex++;
        }

        Dictionary<List<byte>, Banderwagon> commByPath = new(new ListEqualityComparer());
        foreach ((List<byte> path, Banderwagon comm) in allPaths.Zip(commSortedByPath))
        {
            commByPath[path] = comm;
        }

        HashSet<byte[]> subTreesToCreate = UpdatePathsAndReturnSubTreesToCreate(allPaths, allPathsAndZs, subTrees, startStem.BytesAsSpan, endStem.BytesAsSpan);
        InsertSubTreesForSync(subTrees);

        List<byte> pathList = new();
        InsertBranchNodeForSync(pathList.ToArray(), new Commitment(commByPath[pathList]));
        foreach ((byte[]? stem, (ExtPresent extStatus, byte depth)) in depthsAndExtByStem)
        {
            pathList.Clear();
            for (int i = 0; i < depth - 1; i++)
            {
                pathList.Add(stem[i]);
                InsertBranchNodeForSync(pathList.ToArray(), new Commitment(commByPath[pathList]));
            }

            pathList.Add(stem[depth-1]);

            switch (extStatus)
            {
                case ExtPresent.None:
                    InsertPlaceholderForNotPresentStem(stem, pathList.ToArray(), new Commitment());
                    break;
                case ExtPresent.DifferentStem:
                    byte[] otherStem = otherStemsByPrefix[pathList];
                    InsertPlaceholderForNotPresentStem(otherStem, pathList.ToArray(), new(commByPath[pathList]));
                    break;
                case ExtPresent.Present:
                    Commitment internalCommitment = new(commByPath[pathList]);
                    if (!VerifyCommitmentThenInsertStem(pathList.ToArray(), stem, internalCommitment))
                        return false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

        }

        byte[][] allStemsWithoutStartAndEndStems =
            subTrees.Where(x => !x.Path.Bytes.SequenceEqual(startStem.Bytes) && !x.Path.Bytes.SequenceEqual(endStem.Bytes))
                .Select(x => x.Path.Bytes).ToArray();

        int stemIndex = 0;
        Dictionary<byte[], List<byte[]>> stemBatch = new(Bytes.EqualityComparer);
        foreach (byte[] stemPrefix in subTreesToCreate)
        {
            stemBatch.Add(stemPrefix, new List<byte[]>());
            while (stemIndex < allStemsWithoutStartAndEndStems.Length)
            {
                if (Bytes.EqualityComparer.Equals(stemPrefix, allStemsWithoutStartAndEndStems[stemIndex][..stemPrefix.Length]))
                {
                    stemBatch[stemPrefix].Add(allStemsWithoutStartAndEndStems[stemIndex]);
                    stemIndex++;
                }
                else break;
            }
        }

        InsertStemBatchForSync(stemBatch, commByPath);
        bool verification = VerifyVerkleProofStruct(proof.Proof, allPathsAndZs, leafValuesByPathAndZ, commByPath);
        if (!verification) Reset();
        else CommitTree(0);

        return verification;
    }

    private static HashSet<byte[]> UpdatePathsAndReturnSubTreesToCreate(IReadOnlySet<List<byte>> allPaths,
        ISet<(List<byte>, byte)> allPathsAndZs, PathWithSubTree[] stems, ReadOnlySpan<byte> startStem, ReadOnlySpan<byte> endStem)
    {
        ISpanEqualityComparer<byte> comparer = Bytes.SpanEqualityComparer;
        HashSet<byte[]> subTreesToCreate = new(Bytes.EqualityComparer);
        foreach (PathWithSubTree subTree in stems)
        {
            if(comparer.Equals(subTree.Path.Bytes, startStem)) continue;
            if(comparer.Equals(subTree.Path.Bytes, endStem)) continue;
            for (int i = 0; i < 32; i++)
            {
                List<byte> prefix = new(subTree.Path.Bytes[..i]);
                if (allPaths.Contains(prefix))
                {
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
