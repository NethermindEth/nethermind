// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

/// <summary>
/// Persists sparse trie nodes to <see cref="SnapshotBundle"/> after root computation.
/// Replaces <c>PatriciaTree.Commit()</c> in M3. Walks the arena depth-first, writing
/// nodes with <c>FullRlp.Length >= 32</c> to the snapshot bundle.
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

        // Persist the node's FullRlp if it's large enough for a separate DB entry
        if (node.FullRlp is { Length: >= 32 })
            PersistNode(node.FullRlp, path, bundle, address);

        if (node.IsBranch())
        {
            // For folded extension+branch: also persist the inner branch RLP at path + ShortKey
            if (node.HasShortKey() && node.InnerBranchRlp is { Length: >= 32 })
            {
                TreePath branchPath = path.Append(node.ShortKey);
                PersistNode(node.InnerBranchRlp, branchPath, bundle, address);
            }

            // Recurse into revealed children
            TreePath childBasePath = node.HasShortKey() ? path.Append(node.ShortKey) : path;
            TrieMask mask = node.StateMask;
            int childrenStart = node.ChildrenStart;

            for (int n = 0; n < 16; n++)
            {
                if (!mask.IsBitSet(n)) continue;
                int denseIdx = childrenStart + mask.DenseIndex(n);
                SparseChildEntry entry = subtrie.ChildAt(denseIdx);
                if (entry.IsRevealed)
                {
                    TreePath childPath = childBasePath.Append(n);
                    WalkAndCommit(subtrie, entry.ArenaIndex, childPath, bundle, address);
                }
            }
        }
    }

    private static void PersistNode(byte[] fullRlp, TreePath path, SnapshotBundle bundle, Hash256? address)
    {
        Hash256 keccak = Keccak.Compute(fullRlp);
        // TrieNode(NodeType, Hash256, CappedArray<byte>) defaults to isDirty=false, so the node
        // is already sealed at construction. Calling Seal() again would throw "already sealed".
        TrieNode trieNode = new(NodeType.Unknown, keccak, new CappedArray<byte>(fullRlp));

        if (address is null)
            bundle.SetStateNode(path, trieNode);
        else
            bundle.SetStorageNode(address, path, trieNode);
    }
}
