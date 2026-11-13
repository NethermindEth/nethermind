// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>The range proof of a snap tree of <see cref="TrieNode"/>s.</summary>
internal static class PatriciaSnapProof
{
    private const int ExtensionRlpChildIndex = 1;

    /// <summary>Sets the root of <paramref name="tree"/> to the proof's <paramref name="nodes"/>, as boundary proof <see cref="TrieNode"/>s.</summary>
    /// <returns>The nodes with their paths, the root first, for <see cref="StitchBoundaries"/>.</returns>
    public static List<(TrieNode, TreePath)> SetRoot(PatriciaTree tree, IByteArrayList proofs, IReadOnlyList<SnapProofNode> nodes)
    {
        List<(TrieNode, TreePath)> sortedBoundaryList = new(nodes.Count);
        foreach (SnapProofNode proofNode in nodes)
        {
            TrieNode node = new(NodeType.Unknown, proofs[proofNode.ProofIndex].ToArray(), isDirty: true);
            node.IsBoundaryProofNode = true;

            TreePath emptyPath = TreePath.Empty;
            node.ResolveNode(UnknownNodeResolver.Instance, emptyPath);
            node.ResolveKey(UnknownNodeResolver.Instance, ref emptyPath);

            for (int mask = proofNode.ClearedChildren; mask != 0; mask &= mask - 1)
            {
                node.SetChild(BitOperations.TrailingZeroCount(mask), null);
            }

            if (proofNode.ParentIndex >= 0) sortedBoundaryList[proofNode.ParentIndex].Item1.SetChild(proofNode.ChildIndex, node);
            sortedBoundaryList.Add((node, proofNode.Path));
        }

        tree.RootRef = sortedBoundaryList[0].Item1;
        return sortedBoundaryList;
    }

    public static void StitchBoundaries<TEntry>(List<(TrieNode, TreePath)>? sortedBoundaryList, ISnapTree<TEntry> tree) where TEntry : ISnapEntry
    {
        if (sortedBoundaryList is null || sortedBoundaryList.Count == 0)
        {
            return;
        }

        for (int i = sortedBoundaryList.Count - 1; i >= 0; i--)
        {
            (TrieNode node, TreePath path) = sortedBoundaryList[i];
            if (!node.IsPersisted)
            {
                INodeData nodeData = node.NodeData;
                if (nodeData is ExtensionData extensionData)
                {
                    if (IsChildPersisted(node, ref path, extensionData._value, ExtensionRlpChildIndex, tree))
                    {
                        node.IsBoundaryProofNode = false;
                    }
                }
                else if (nodeData is BranchData branchData)
                {
                    bool isBoundaryProofNode = false;
                    int ci = 0;
                    foreach (object? o in branchData.Branches)
                    {
                        if (!IsChildPersisted(node, ref path, o, ci, tree))
                        {
                            isBoundaryProofNode = true;
                            break;
                        }
                        ci++;
                    }

                    node.IsBoundaryProofNode = isBoundaryProofNode;
                }

                //leaves as a part of boundary are only added if they are outside the processed range,
                //therefore they will not be persisted during current Commit. Still it is possible, they have already
                //been persisted when processing a range they belong, so it is needed to do a check here.
                //Without it, there is a risk that the whole dependant path (including root) will not be eventually stitched and persisted
                //leading to TrieNodeException after sync (as healing may not get to heal the particular storage trie)
                if (node.IsLeaf)
                {
                    node.IsPersisted = tree.IsPersisted(path, node.Keccak);
                    node.IsBoundaryProofNode = !node.IsPersisted;
                }
            }
        }
    }

    private static bool IsChildPersisted<TEntry>(TrieNode node, ref TreePath nodePath, object? child, int childIndex, ISnapTree<TEntry> tree) where TEntry : ISnapEntry
    {
        if (child is TrieNode childNode)
        {
            return !childNode.IsBoundaryProofNode;
        }

        ValueHash256 childKeccak;
        if (child is Hash256 hash)
        {
            childKeccak = hash.ValueHash256;
        }
        else if (!node.GetChildHashAsValueKeccak(childIndex, out childKeccak))
        {
            return true;
        }

        int previousPathLength = node.AppendChildPath(ref nodePath, childIndex);
        try
        {
            return tree.IsPersisted(nodePath, childKeccak);
        }
        finally
        {
            nodePath.TruncateMut(previousPathLength);
        }
    }
}
