// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.StateDiff.Core.Diff;

public sealed partial class TrieDiffWalker
{
    private void DiffBranches(TrieNode oldBranch, TrieNode newBranch, ref TreePath path, ResolverPair resolvers, bool isStorage)
    {
        // Both branches still exist at this path — count their RLP bytes on both
        // sides so the net per-CF delta matches the legacy walker (which records
        // an "added" entry for the new branch and a "removed" entry for the old).
        RecordNodeBytes(oldBranch.FullRlp.Length, isStorage, added: false);
        RecordNodeBytes(newBranch.FullRlp.Length, isStorage, added: true);

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

            if (oldIsNull && newIsNull) continue;

            int prevLen = path.Length;
            path.AppendMut(i);
            DiffBranchChild(oldBranch, newBranch, i, oldChildHash, newChildHash,
                oldIsNull, newIsNull, ref path, resolvers, isStorage);
            path.TruncateMut(prevLen);
        }
    }

    private void DiffBranchChild(TrieNode oldBranch, TrieNode newBranch, int i,
        Hash256? oldChildHash, Hash256? newChildHash, bool oldIsNull, bool newIsNull,
        ref TreePath path, ResolverPair resolvers, bool isStorage)
    {
        if (oldChildHash is not null && newChildHash is not null)
        {
            DiffSubtree(oldChildHash, newChildHash, ref path, resolvers, isStorage);
            return;
        }

        if (oldIsNull)
        {
            CollectBranchSlotSide(newBranch, i, newChildHash, ref path, resolvers, isStorage, added: true);
            return;
        }

        if (newIsNull)
        {
            CollectBranchSlotSide(oldBranch, i, oldChildHash, ref path, resolvers, isStorage, added: false);
            return;
        }

        TrieNode? oldChild = oldBranch.GetChildWithChildPath(resolvers.Old, ref path, i);
        TrieNode? newChild = newBranch.GetChildWithChildPath(resolvers.New, ref path, i);

        // Cache-induced null asymmetry on inline children: GetChildWithChildPath consults
        // _nodeData[i] before re-parsing RLP, and that cache state can diverge between Old
        // and New resolvers (UnresolveChild wipes persisted parents to null but leaves
        // dirty inline TrieNode references intact). Re-derive from the parent's RLP via
        // GetInlineNodeRlp, which is a pure function of the branch bytes and identical
        // across resolvers — guarantees both sides see the same leaves.
        if (oldChild is null && !oldBranch.IsChildNull(i))
        {
            byte[]? inlineRlp = oldBranch.GetInlineNodeRlp(i);
            if (inlineRlp is not null) oldChild = new TrieNode(NodeType.Unknown, inlineRlp);
        }
        if (newChild is null && !newBranch.IsChildNull(i))
        {
            byte[]? inlineRlp = newBranch.GetInlineNodeRlp(i);
            if (inlineRlp is not null) newChild = new TrieNode(NodeType.Unknown, inlineRlp);
        }

        if (oldChild is not null && newChild is not null)
        {
            oldChild.ResolveNode(resolvers.Old, in path);
            newChild.ResolveNode(resolvers.New, in path);
            DiffNodes(oldChild, newChild, ref path, resolvers, isStorage);
        }
        else if (oldChild is not null)
        {
            oldChild.ResolveNode(resolvers.Old, in path);
            CollectSubtree(oldChild, ref path, resolvers, isStorage, added: false);
        }
        else if (newChild is not null)
        {
            newChild.ResolveNode(resolvers.New, in path);
            CollectSubtree(newChild, ref path, resolvers, isStorage, added: true);
        }
    }

    private void CollectBranchSlotSide(TrieNode branch, int i, Hash256? childHash,
        ref TreePath path, ResolverPair resolvers, bool isStorage, bool added)
    {
        ITrieNodeResolver side = resolvers.Pick(added);
        if (childHash is not null)
        {
            TrieNode child = side.FindCachedOrUnknown(in path, childHash);
            child.ResolveNode(side, in path);
            CollectSubtree(child, ref path, resolvers, isStorage, added);
            return;
        }

        TrieNode? inlineChild = branch.GetChildWithChildPath(side, ref path, i);
        if (inlineChild is null && !branch.IsChildNull(i))
        {
            byte[]? inlineRlp = branch.GetInlineNodeRlp(i);
            if (inlineRlp is not null) inlineChild = new TrieNode(NodeType.Unknown, inlineRlp);
        }
        if (inlineChild is not null)
        {
            inlineChild.ResolveNode(side, in path);
            CollectSubtree(inlineChild, ref path, resolvers, isStorage, added);
        }
    }

    private void DiffMismatchedNodes(TrieNode oldNode, TrieNode newNode, ref TreePath path,
        ResolverPair resolvers, bool isStorage)
    {
        Dictionary<ValueHash256, (TrieNode Leaf, TreePath Path)> oldLeaves = [];
        Dictionary<ValueHash256, (TrieNode Leaf, TreePath Path)> newLeaves = [];

        CollectSubtreeForDiff(oldNode, ref path, resolvers, isStorage, added: false, oldLeaves);
        CollectSubtreeForDiff(newNode, ref path, resolvers, isStorage, added: true, newLeaves);

        foreach (KeyValuePair<ValueHash256, (TrieNode Leaf, TreePath Path)> kvp in newLeaves)
        {
            ValueHash256 fullPath = kvp.Key;
            (TrieNode newLeaf, TreePath newLeafPath) = kvp.Value;

            if (oldLeaves.Remove(fullPath, out (TrieNode Leaf, TreePath Path) oldEntry))
            {
                // Account leaves still need the semantic diff (code-hash transitions and
                // storage-root recursion). Storage leaves at matching paths have nothing
                // semantic to compare — slot presence is unchanged across both sides.
                if (!isStorage)
                {
                    TreePath leafPath = oldEntry.Path;
                    DecodeAndDiffAccountLeaves(oldEntry.Leaf, newLeaf, ref leafPath, resolvers);
                }
            }
            else
            {
                TreePath leafPath = newLeafPath;
                CollectLeaf(newLeaf, ref leafPath, added: true, isStorage, resolvers);
            }
        }

        foreach (KeyValuePair<ValueHash256, (TrieNode Leaf, TreePath Path)> kvp in oldLeaves)
        {
            (TrieNode oldLeaf, TreePath oldLeafPath) = kvp.Value;
            TreePath leafPath = oldLeafPath;
            CollectLeaf(oldLeaf, ref leafPath, added: false, isStorage, resolvers);
        }
    }
}
