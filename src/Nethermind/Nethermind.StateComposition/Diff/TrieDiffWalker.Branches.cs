// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.StateComposition.Diff;

internal sealed partial class TrieDiffWalker
{
    private void DiffBranches(TrieNode oldBranch, TrieNode newBranch, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        RecordNode(NodeType.Branch, oldBranch.FullRlp.Length, isStorage, added: false);
        RecordNode(NodeType.Branch, newBranch.FullRlp.Length, isStorage, added: true);

        if (trackDepth)
        {
            int d = Math.Min(depth, 15);
            RecordDepthBranch(oldBranch, d, isStorage, added: false);
            RecordDepthBranch(newBranch, d, isStorage, added: true);
        }

        int childDepth = depth + 1;
        for (int i = 0; i < 16; i++)
        {
            Hash256? oldChildHash = oldBranch.GetChildHash(i);
            Hash256? newChildHash = newBranch.GetChildHash(i);

            if (oldChildHash is not null && newChildHash is not null && oldChildHash == newChildHash)
                continue;

            // GetChildHash returns null for BOTH empty slots AND inline nodes.
            // Use IsChildNull to distinguish.
            bool oldIsNull = oldChildHash is null && oldBranch.IsChildNull(i);
            bool newIsNull = newChildHash is null && newBranch.IsChildNull(i);

            if (oldIsNull && newIsNull) continue; // Both empty

            int prevLen = path.Length;
            path.AppendMut(i);
            DiffBranchChild(oldBranch, newBranch, i, oldChildHash, newChildHash,
                oldIsNull, newIsNull, ref path, resolver, isStorage, childDepth);
            path.TruncateMut(prevLen);
        }
    }

    private void DiffBranchChild(TrieNode oldBranch, TrieNode newBranch, int i,
        Hash256? oldChildHash, Hash256? newChildHash, bool oldIsNull, bool newIsNull,
        ref TreePath path, ITrieNodeResolver resolver, bool isStorage, int childDepth)
    {
        if (oldChildHash is not null && newChildHash is not null)
        {
            DiffSubtree(oldChildHash, newChildHash, ref path, resolver, isStorage, childDepth);
            return;
        }

        if (oldIsNull)
        {
            CollectBranchSlotSide(newBranch, i, newChildHash, ref path, resolver, isStorage, added: true, childDepth);
            return;
        }

        if (newIsNull)
        {
            CollectBranchSlotSide(oldBranch, i, oldChildHash, ref path, resolver, isStorage, added: false, childDepth);
            return;
        }

        TrieNode? oldChild = oldBranch.GetChildWithChildPath(resolver, ref path, i);
        TrieNode? newChild = newBranch.GetChildWithChildPath(resolver, ref path, i);

        if (oldChild is not null && newChild is not null)
        {
            oldChild.ResolveNode(resolver, in path);
            newChild.ResolveNode(resolver, in path);
            DiffNodes(oldChild, newChild, ref path, resolver, isStorage, childDepth);
        }
        else if (oldChild is not null)
        {
            oldChild.ResolveNode(resolver, in path);
            CollectSubtree(oldChild, ref path, resolver, isStorage, added: false, childDepth);
        }
        else if (newChild is not null)
        {
            newChild.ResolveNode(resolver, in path);
            CollectSubtree(newChild, ref path, resolver, isStorage, added: true, childDepth);
        }
    }

    private void CollectBranchSlotSide(TrieNode branch, int i, Hash256? childHash,
        ref TreePath path, ITrieNodeResolver resolver, bool isStorage, bool added, int childDepth)
    {
        if (childHash is not null)
        {
            TrieNode child = resolver.FindCachedOrUnknown(in path, childHash);
            child.ResolveNode(resolver, in path);
            CollectSubtree(child, ref path, resolver, isStorage, added, childDepth);
            return;
        }

        TrieNode? inlineChild = branch.GetChildWithChildPath(resolver, ref path, i);
        if (inlineChild is not null)
        {
            inlineChild.ResolveNode(resolver, in path);
            CollectSubtree(inlineChild, ref path, resolver, isStorage, added, childDepth);
        }
    }

    private void DiffMismatchedNodes(TrieNode oldNode, TrieNode newNode, ref TreePath path,
        ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        Dictionary<ValueHash256, (TrieNode Leaf, TreePath Path)> oldLeaves = [];
        Dictionary<ValueHash256, (TrieNode Leaf, TreePath Path)> newLeaves = [];

        CollectSubtreeForDiff(oldNode, ref path, resolver, isStorage, added: false, oldLeaves, depth);
        CollectSubtreeForDiff(newNode, ref path, resolver, isStorage, added: true, newLeaves, depth);

        foreach (KeyValuePair<ValueHash256, (TrieNode Leaf, TreePath Path)> kvp in newLeaves)
        {
            ValueHash256 fullPath = kvp.Key;
            (TrieNode newLeaf, TreePath newLeafPath) = kvp.Value;

            if (oldLeaves.Remove(fullPath, out (TrieNode Leaf, TreePath Path) oldEntry))
            {
                if (!isStorage)
                {
                    TreePath leafPath = oldEntry.Path;
                    DecodeAndDiffAccountLeaves(oldEntry.Leaf, newLeaf, ref leafPath);
                }
            }
            else
            {
                TreePath leafPath = newLeafPath;
                CollectLeaf(newLeaf, ref leafPath, added: true, isStorage);
            }
        }

        foreach (KeyValuePair<ValueHash256, (TrieNode Leaf, TreePath Path)> kvp in oldLeaves)
        {
            (TrieNode oldLeaf, TreePath oldLeafPath) = kvp.Value;
            TreePath leafPath = oldLeafPath;
            CollectLeaf(oldLeaf, ref leafPath, added: false, isStorage);
        }
    }
}
