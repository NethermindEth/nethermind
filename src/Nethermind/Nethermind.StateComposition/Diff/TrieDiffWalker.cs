// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Diff;

/// <summary>
/// Walks two committed state roots and computes exact adds/removes for every metric.
/// Content-addressed property: if hash(old) == hash(new), entire subtree identical → skip.
/// Read-only — uses only <see cref="ITrieNodeResolver.FindCachedOrUnknown"/>.
/// </summary>
internal sealed partial class TrieDiffWalker(ITrieNodeResolver resolver, bool trackDepth = false)
{
    private readonly DepthDelta _depthDelta = new();

    private int _accountsAdded, _accountsRemoved;
    private int _contractsAdded, _contractsRemoved;
    private int _accountTrieBranchesAdded, _accountTrieBranchesRemoved;
    private int _accountTrieExtensionsAdded, _accountTrieExtensionsRemoved;
    private int _accountTrieLeavesAdded, _accountTrieLeavesRemoved;
    private long _accountTrieBytesAdded, _accountTrieBytesRemoved;
    private int _storageTrieBranchesAdded, _storageTrieBranchesRemoved;
    private int _storageTrieExtensionsAdded, _storageTrieExtensionsRemoved;
    private int _storageTrieLeavesAdded, _storageTrieLeavesRemoved;
    private long _storageTrieBytesAdded, _storageTrieBytesRemoved;
    private long _storageSlotsAdded, _storageSlotsRemoved;
    private int _contractsWithStorageAdded, _contractsWithStorageRemoved;
    private int _emptyAccountsAdded, _emptyAccountsRemoved;

    /// <summary>
    /// Compute exact diff between two committed state roots.
    /// Both roots must already be persisted and resolvable via the resolver.
    /// </summary>
    public TrieDiff ComputeDiff(Hash256? oldRoot, Hash256? newRoot)
    {
        ResetCounters();
        if (trackDepth) _depthDelta.Clear();

        Hash256? oldHash = NormalizeHash(oldRoot);
        Hash256? newHash = NormalizeHash(newRoot);

        if (oldHash == newHash) return default;

        TreePath path = TreePath.Empty;
        DiffSubtree(oldHash, newHash, ref path, resolver, isStorage: false, depth: 0);

        return new TrieDiff(
            _accountsAdded, _accountsRemoved,
            _contractsAdded, _contractsRemoved,
            _accountTrieBranchesAdded, _accountTrieBranchesRemoved,
            _accountTrieExtensionsAdded, _accountTrieExtensionsRemoved,
            _accountTrieLeavesAdded, _accountTrieLeavesRemoved,
            _accountTrieBytesAdded, _accountTrieBytesRemoved,
            _storageTrieBranchesAdded, _storageTrieBranchesRemoved,
            _storageTrieExtensionsAdded, _storageTrieExtensionsRemoved,
            _storageTrieLeavesAdded, _storageTrieLeavesRemoved,
            _storageTrieBytesAdded, _storageTrieBytesRemoved,
            _storageSlotsAdded, _storageSlotsRemoved,
            _contractsWithStorageAdded, _contractsWithStorageRemoved,
            _emptyAccountsAdded, _emptyAccountsRemoved,
            DepthDelta: trackDepth ? _depthDelta : null
        );
    }

    /// <summary>
    /// Diff two subtree roots. Resolves nodes and dispatches to type-specific comparison.
    /// If both hashes are equal → skip (content-addressed fast path).
    /// If only one exists → collect entire subtree as added or removed.
    /// </summary>
    private void DiffSubtree(Hash256? oldHash, Hash256? newHash, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        // Fast path: identical subtrees
        if (oldHash == newHash) return;

        // One side is empty → collect entire other side
        if (oldHash is null)
        {
            TrieNode newNode = resolver.FindCachedOrUnknown(in path, newHash!);
            newNode.ResolveNode(resolver, in path);
            CollectSubtree(newNode, ref path, resolver, isStorage, added: true, depth: depth);
            return;
        }

        if (newHash is null)
        {
            TrieNode oldNode = resolver.FindCachedOrUnknown(in path, oldHash);
            oldNode.ResolveNode(resolver, in path);
            CollectSubtree(oldNode, ref path, resolver, isStorage, added: false, depth: depth);
            return;
        }

        // Both exist and differ → resolve and compare
        TrieNode oldResolved = resolver.FindCachedOrUnknown(in path, oldHash);
        oldResolved.ResolveNode(resolver, in path);
        TrieNode newResolved = resolver.FindCachedOrUnknown(in path, newHash);
        newResolved.ResolveNode(resolver, in path);

        DiffNodes(oldResolved, newResolved, ref path, resolver, isStorage, depth);
    }

    /// <summary>
    /// Compare two resolved nodes. Dispatches based on matching node types.
    /// On type mismatch, collects both subtrees independently.
    /// </summary>
    private void DiffNodes(TrieNode oldNode, TrieNode newNode, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        if (oldNode.NodeType != newNode.NodeType)
        {
            // Type mismatch (e.g., leaf → extension+branch on insert).
            // Can't just collect both subtrees independently — that would double-count
            // accounts/contracts/slots that exist in both. Instead, enumerate leaves
            // from both sides, match by full path, and diff semantically.
            DiffMismatchedNodes(oldNode, newNode, ref path, resolver, isStorage, depth);
            return;
        }

        switch (oldNode.NodeType)
        {
            case NodeType.Branch:
                DiffBranches(oldNode, newNode, ref path, resolver, isStorage, depth);
                break;
            case NodeType.Extension:
                DiffExtensions(oldNode, newNode, ref path, resolver, isStorage, depth);
                break;
            case NodeType.Leaf:
                DiffLeaves(oldNode, newNode, ref path, resolver, isStorage, depth);
                break;
        }
    }

    /// <summary>
    /// Normalize empty tree hash to null for uniform comparison.
    /// Content-addressed: EmptyTreeHash means "no trie" = null.
    /// </summary>
    private static Hash256? NormalizeHash(Hash256? hash)
    {
        if (hash is null) return null;
        return hash == Keccak.EmptyTreeHash ? null : hash;
    }

    /// <summary>
    /// Count the non-null children of a branch node. Used for branch-occupancy histogram deltas.
    /// </summary>
    private static int CountBranchChildren(TrieNode branch)
    {
        int count = 0;
        for (int i = 0; i < 16; i++)
        {
            if (!branch.IsChildNull(i)) count++;
        }
        return count;
    }

    private void ResetCounters()
    {
        _accountsAdded = 0; _accountsRemoved = 0;
        _contractsAdded = 0; _contractsRemoved = 0;
        _accountTrieBranchesAdded = 0; _accountTrieBranchesRemoved = 0;
        _accountTrieExtensionsAdded = 0; _accountTrieExtensionsRemoved = 0;
        _accountTrieLeavesAdded = 0; _accountTrieLeavesRemoved = 0;
        _accountTrieBytesAdded = 0; _accountTrieBytesRemoved = 0;
        _storageTrieBranchesAdded = 0; _storageTrieBranchesRemoved = 0;
        _storageTrieExtensionsAdded = 0; _storageTrieExtensionsRemoved = 0;
        _storageTrieLeavesAdded = 0; _storageTrieLeavesRemoved = 0;
        _storageTrieBytesAdded = 0; _storageTrieBytesRemoved = 0;
        _storageSlotsAdded = 0; _storageSlotsRemoved = 0;
        _contractsWithStorageAdded = 0; _contractsWithStorageRemoved = 0;
        _emptyAccountsAdded = 0; _emptyAccountsRemoved = 0;
    }
}
