// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

/// <summary>
/// Persists sparse trie nodes to <see cref="SnapshotBundle"/> after root computation.
/// Walks the arena depth-first, writing nodes with <c>FullRlp.Length >= 32</c> to the snapshot bundle.
/// For branch-with-ShortKey (folded extension+branch), persists BOTH the extension
/// wrapper and the inner branch at their respective paths.
/// </summary>
public static class SparseTrieSnapshotCommitter
{
    public static void CommitAccountTrie(SparseSubtrie subtrie, SnapshotBundle bundle)
    {
        if (subtrie.Root < 0 || subtrie.NodeAt(subtrie.Root).IsEmpty()) return;
        WalkAndCommit(subtrie, subtrie.Root, TreePath.Empty, bundle, address: null);
    }

    public static void CommitStorageTrie(SparseSubtrie subtrie, SnapshotBundle bundle, Hash256 accountPathHash)
    {
        if (subtrie.Root < 0 || subtrie.NodeAt(subtrie.Root).IsEmpty()) return;
        WalkAndCommit(subtrie, subtrie.Root, TreePath.Empty, bundle, accountPathHash);
    }

    private static void WalkAndCommit(SparseSubtrie subtrie, int nodeIdx, TreePath path,
        SnapshotBundle bundle, Hash256? address)
    {
        ref SparseTrieNode node = ref subtrie.NodeAt(nodeIdx);
        if (!node.IsCached()) return;

        // CRITICAL with cross-block reuse: only persist nodes whose FullRlp is set AND not already
        // persisted in a prior block. We CLEAR FullRlp after PersistNode so re-walking doesn't
        // re-persist. This is safe because parent encoding reads CachedRlp (which keeps the hash),
        // not FullRlp. Without this, every cross-block walk re-persists ~10K-50K nodes, costing
        // hundreds of ms per block.
        if (node.FullRlp is { Length: >= 32 })
        {
            // Reuse the hash from CachedRlp if it's already a hash (avoids a second keccak).
            Hash256 keccak = node.CachedRlp.IsHash() ? node.CachedRlp.AsHash() : Keccak.Compute(node.FullRlp);
            PersistNodeWithHash(keccak, node.FullRlp, path, bundle, address);
            node.FullRlp = null; // mark as persisted
        }

        if (node.IsBranch())
        {
            // For folded extension+branch: also persist the inner branch RLP at path + ShortKey
            if (node.HasShortKey() && node.InnerBranchRlp is { Length: >= 32 })
            {
                TreePath branchPath = path.Append(node.ShortKey);
                Hash256 innerKeccak = Keccak.Compute(node.InnerBranchRlp);
                PersistNodeWithHash(innerKeccak, node.InnerBranchRlp, branchPath, bundle, address);
                node.InnerBranchRlp = null;
            }

            // Recurse only into children that have FullRlp set (= encoded this block).
            // Invariant: if a child is "clean" (FullRlp null after the previous commit),
            // it was either never modified or already persisted; its entire subtree is
            // clean too thanks to dirty-propagates-upward in MarkDirty + EncodeBranch.
            // This is the same dirty-path-only optimization HashNode uses.
            TreePath childBasePath = node.HasShortKey() ? path.Append(node.ShortKey) : path;
            TrieMask mask = node.StateMask;
            int childrenStart = node.ChildrenStart;

            for (int n = 0; n < 16; n++)
            {
                if (!mask.IsBitSet(n)) continue;
                int denseIdx = childrenStart + mask.DenseIndex(n);
                SparseChildEntry entry = subtrie.ChildAt(denseIdx);
                if (!entry.IsRevealed) continue;
                ref SparseTrieNode child = ref subtrie.NodeAt(entry.ArenaIndex);
                // Skip clean subtrees: their FullRlp and InnerBranchRlp are null AND they
                // can't contain dirty descendants because parent encoding only sets FullRlp
                // when descendants did.
                if (child.FullRlp is null && child.InnerBranchRlp is null) continue;
                TreePath childPath = childBasePath.Append(n);
                WalkAndCommit(subtrie, entry.ArenaIndex, childPath, bundle, address);
            }
        }
    }

    private static void PersistNodeWithHash(Hash256 keccak, byte[] fullRlp, TreePath path, SnapshotBundle bundle, Hash256? address)
    {
        // TrieNode(NodeType, Hash256, CappedArray<byte>) defaults to isDirty=false, so the node
        // is already sealed at construction. Calling Seal() again would throw "already sealed".
        TrieNode trieNode = new(NodeType.Unknown, keccak, new CappedArray<byte>(fullRlp));

        if (address is null)
            bundle.SetStateNode(path, trieNode);
        else
            bundle.SetStorageNode(address, path, trieNode);
    }
}
