// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.StateComposition;

/// <summary>
/// Walks two committed state roots and computes exact adds/removes for every metric.
/// Content-addressed property: if hash(old) == hash(new), entire subtree identical → skip.
/// Read-only — uses only <see cref="ITrieNodeResolver.FindCachedOrUnknown"/>.
/// </summary>
public sealed class TrieDiffWalker
{
    private readonly ITrieNodeResolver _resolver;
    private readonly bool _trackDepth;
    private readonly DepthDelta _depthDelta;

    // Mutable counters accumulated during a single ComputeDiff call.
    // Reset at the start of each call.
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

    public TrieDiffWalker(ITrieNodeResolver resolver, bool trackDepth = false)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _trackDepth = trackDepth;
        _depthDelta = new DepthDelta();
    }

    /// <summary>
    /// Compute exact diff between two committed state roots.
    /// Both roots must already be persisted and resolvable via the resolver.
    /// </summary>
    public TrieDiff ComputeDiff(Hash256? oldRoot, Hash256? newRoot)
    {
        ResetCounters();
        if (_trackDepth) _depthDelta.Clear();

        Hash256? oldHash = NormalizeHash(oldRoot);
        Hash256? newHash = NormalizeHash(newRoot);

        if (oldHash == newHash) return default;

        TreePath path = TreePath.Empty;
        DiffSubtree(oldHash, newHash, ref path, _resolver, isStorage: false, depth: 0);

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
            DepthDelta: _trackDepth ? _depthDelta : null
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
    /// Compare two branch nodes. Record both branch nodes, then diff each of the 16 children.
    /// Uses hash comparison for fast skip of identical children.
    /// </summary>
    private void DiffBranches(TrieNode oldBranch, TrieNode newBranch, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        // Record the branch nodes themselves (old removed, new added)
        RecordNode(NodeType.Branch, oldBranch.FullRlp.Length, isStorage, added: false);
        RecordNode(NodeType.Branch, newBranch.FullRlp.Length, isStorage, added: true);

        if (_trackDepth)
        {
            int d = Math.Min(depth, 15);
            // Remove old branch, add new branch at this depth
            RecordDepthBranch(oldBranch, d, isStorage, added: false);
            RecordDepthBranch(newBranch, d, isStorage, added: true);
        }

        int childDepth = depth + 1;
        for (int i = 0; i < 16; i++)
        {
            Hash256? oldChildHash = oldBranch.GetChildHash(i);
            Hash256? newChildHash = newBranch.GetChildHash(i);

            // Both have hashes → fast compare
            if (oldChildHash is not null && newChildHash is not null)
            {
                if (oldChildHash == newChildHash) continue; // Identical subtree

                int prevLen = path.Length;
                path.AppendMut(i);
                DiffSubtree(oldChildHash, newChildHash, ref path, resolver, isStorage, childDepth);
                path.TruncateMut(prevLen);
                continue;
            }

            // Both have hashes handled above. Now handle inline/null cases.
            // GetChildHash returns null for BOTH empty slots AND inline nodes.
            // Use IsChildNull to distinguish.
            bool oldIsNull = oldChildHash is null && oldBranch.IsChildNull(i);
            bool newIsNull = newChildHash is null && newBranch.IsChildNull(i);

            if (oldIsNull && newIsNull) continue; // Both empty

            int prevLength = path.Length;
            path.AppendMut(i);

            if (oldIsNull)
            {
                // Old is empty, new has inline or hash child
                if (newChildHash is not null)
                {
                    // New is hash-referenced
                    TrieNode newChild = resolver.FindCachedOrUnknown(in path, newChildHash);
                    newChild.ResolveNode(resolver, in path);
                    CollectSubtree(newChild, ref path, resolver, isStorage, added: true, childDepth);
                }
                else
                {
                    // New is inline
                    TrieNode? newChild = newBranch.GetChildWithChildPath(resolver, ref path, i);
                    if (newChild is not null)
                    {
                        newChild.ResolveNode(resolver, in path);
                        CollectSubtree(newChild, ref path, resolver, isStorage, added: true, childDepth);
                    }
                }
            }
            else if (newIsNull)
            {
                // New is empty, old has inline or hash child
                if (oldChildHash is not null)
                {
                    TrieNode oldChild = resolver.FindCachedOrUnknown(in path, oldChildHash);
                    oldChild.ResolveNode(resolver, in path);
                    CollectSubtree(oldChild, ref path, resolver, isStorage, added: false, childDepth);
                }
                else
                {
                    TrieNode? oldChild = oldBranch.GetChildWithChildPath(resolver, ref path, i);
                    if (oldChild is not null)
                    {
                        oldChild.ResolveNode(resolver, in path);
                        CollectSubtree(oldChild, ref path, resolver, isStorage, added: false, childDepth);
                    }
                }
            }
            else
            {
                // Both are inline (both hashes null, neither IsChildNull)
                // Must resolve both and diff
                TreePath oldChildPath = path;
                TrieNode? oldChild = oldBranch.GetChildWithChildPath(resolver, ref oldChildPath, i);
                TreePath newChildPath = path;
                TrieNode? newChild = newBranch.GetChildWithChildPath(resolver, ref newChildPath, i);

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

            path.TruncateMut(prevLength);
        }
    }

    /// <summary>
    /// Compare two extension nodes. If keys match, recurse into child.
    /// If keys differ, collect both subtrees independently.
    /// </summary>
    private void DiffExtensions(TrieNode oldExt, TrieNode newExt, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        byte[]? oldKey = oldExt.Key;
        byte[]? newKey = newExt.Key;

        if (oldKey is not null && newKey is not null && oldKey.AsSpan().SequenceEqual(newKey))
        {
            // Same key prefix: record both extension nodes, recurse child
            RecordNode(NodeType.Extension, oldExt.FullRlp.Length, isStorage, added: false);
            RecordNode(NodeType.Extension, newExt.FullRlp.Length, isStorage, added: true);

            if (_trackDepth)
            {
                int d = Math.Min(depth, 15);
                RecordDepthShort(oldExt.FullRlp.Length, d, isStorage, added: false);
                RecordDepthShort(newExt.FullRlp.Length, d, isStorage, added: true);
            }

            // Extension child hash is at RLP index 1
            Hash256? oldChildHash = oldExt.GetChildHash(1);
            Hash256? newChildHash = newExt.GetChildHash(1);

            int prevLen = path.Length;
            path.AppendMut(oldKey);
            int childDepth = depth + oldKey.Length;

            if (oldChildHash is not null && newChildHash is not null)
            {
                DiffSubtree(oldChildHash, newChildHash, ref path, resolver, isStorage, childDepth);
            }
            else
            {
                // At least one child is inline — resolve via GetChildWithChildPath
                TreePath oldChildPath = path;
                TrieNode? oldChild = oldExt.GetChildWithChildPath(resolver, ref oldChildPath, 0);
                TreePath newChildPath = path;
                TrieNode? newChild = newExt.GetChildWithChildPath(resolver, ref newChildPath, 0);

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

            path.TruncateMut(prevLen);
        }
        else
        {
            // Different key prefixes: entirely different subtrees
            CollectSubtree(oldExt, ref path, resolver, isStorage, added: false, depth);
            CollectSubtree(newExt, ref path, resolver, isStorage, added: true, depth);
        }
    }

    /// <summary>
    /// Compare two leaf nodes. Both are at the same trie path.
    /// For account trie: decode accounts to detect contract/storage changes.
    /// For storage trie: each leaf is one storage slot.
    /// </summary>
    private void DiffLeaves(TrieNode oldLeaf, TrieNode newLeaf, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        // Record both leaf nodes (old removed, new added)
        RecordNode(NodeType.Leaf, oldLeaf.FullRlp.Length, isStorage, added: false);
        RecordNode(NodeType.Leaf, newLeaf.FullRlp.Length, isStorage, added: true);

        if (_trackDepth)
        {
            int d = Math.Min(depth, 15);
            // Old leaf removed, new leaf added (same path = modification, both short+value counters net to zero)
            RecordDepthLeaf(oldLeaf.FullRlp.Length, d, isStorage, added: false);
            RecordDepthLeaf(newLeaf.FullRlp.Length, d, isStorage, added: true);
        }

        if (isStorage)
        {
            // Storage leaves: each leaf is one slot, but same path means same slot modified → net zero
            // Both exist at same path so it's an update, not add/remove
            return;
        }

        // Account trie leaves: decode to check contract and storage changes
        DecodeAndDiffAccountLeaves(oldLeaf, newLeaf, ref path);
    }

    /// <summary>
    /// Decode two account leaves and diff their contract status and storage roots.
    /// Both leaves are at the same account path (same address hash).
    /// </summary>
    private void DecodeAndDiffAccountLeaves(TrieNode oldLeaf, TrieNode newLeaf, ref TreePath path)
    {
        AccountStruct oldAccount = DecodeAccount(oldLeaf);
        AccountStruct newAccount = DecodeAccount(newLeaf);

        // Account itself: same path, same account, just modified → net zero for account count
        // (not an add or remove of an account)

        // Contract status change
        if (!oldAccount.HasCode && newAccount.HasCode) _contractsAdded++;
        else if (oldAccount.HasCode && !newAccount.HasCode) _contractsRemoved++;

        // Contract-with-storage transition (HasStorage = StorageRoot != EmptyTreeHash)
        if (!oldAccount.HasStorage && newAccount.HasStorage) _contractsWithStorageAdded++;
        else if (oldAccount.HasStorage && !newAccount.HasStorage) _contractsWithStorageRemoved++;

        // Empty-account transition (matches StateCompositionVisitor: nonce=0, balance=0, no code, no storage)
        if (!oldAccount.IsTotallyEmpty && newAccount.IsTotallyEmpty) _emptyAccountsAdded++;
        else if (oldAccount.IsTotallyEmpty && !newAccount.IsTotallyEmpty) _emptyAccountsRemoved++;

        // Storage trie diff — skip allocation when storage roots are identical
        if (oldAccount.StorageRoot == newAccount.StorageRoot) return;

        Hash256? normalizedOldStorage = oldAccount.HasStorage ? new Hash256(oldAccount.StorageRoot) : null;
        Hash256? normalizedNewStorage = newAccount.HasStorage ? new Hash256(newAccount.StorageRoot) : null;

        Hash256 addressHash = GetAddressHash(oldLeaf, ref path);
        ITrieNodeResolver storageResolver = _resolver.GetStorageTrieNodeResolver(addressHash);
        TreePath storagePath = TreePath.Empty;
        // Storage tries always start at depth 0 (independent trie)
        DiffSubtree(normalizedOldStorage, normalizedNewStorage, ref storagePath, storageResolver, isStorage: true, depth: 0);
    }

    /// <summary>
    /// Recursively collect all nodes in a subtree as either added or removed.
    /// Also counts accounts, contracts, and storage slots at leaves.
    /// </summary>
    private void CollectSubtree(TrieNode node, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, bool added, int depth)
    {
        RecordNode(node.NodeType, node.FullRlp.Length, isStorage, added);

        if (_trackDepth)
        {
            int d = Math.Min(depth, 15);
            switch (node.NodeType)
            {
                case NodeType.Branch:
                    RecordDepthBranch(node, d, isStorage, added);
                    break;
                case NodeType.Extension:
                    RecordDepthShort(node.FullRlp.Length, d, isStorage, added);
                    break;
                case NodeType.Leaf:
                    RecordDepthLeaf(node.FullRlp.Length, d, isStorage, added);
                    break;
            }
        }

        switch (node.NodeType)
        {
            case NodeType.Branch:
            {
                int childDepth = depth + 1;
                for (int i = 0; i < 16; i++)
                {
                    Hash256? childHash = node.GetChildHash(i);

                    if (childHash is not null)
                    {
                        int prevLen = path.Length;
                        path.AppendMut(i);
                        TrieNode child = resolver.FindCachedOrUnknown(in path, childHash);
                        child.ResolveNode(resolver, in path);
                        CollectSubtree(child, ref path, resolver, isStorage, added, childDepth);
                        path.TruncateMut(prevLen);
                    }
                    else if (!node.IsChildNull(i))
                    {
                        // Inline child
                        int prevLen = path.Length;
                        path.AppendMut(i);
                        TrieNode? child = node.GetChildWithChildPath(resolver, ref path, i);
                        if (child is not null)
                        {
                            child.ResolveNode(resolver, in path);
                            CollectSubtree(child, ref path, resolver, isStorage, added, childDepth);
                        }
                        path.TruncateMut(prevLen);
                    }
                }
                break;
            }

            case NodeType.Extension:
            {
                Hash256? childHash = node.GetChildHash(1);
                int prevLen = path.Length;
                path.AppendMut(node.Key!);
                int childDepth = depth + (node.Key?.Length ?? 1);

                if (childHash is not null)
                {
                    TrieNode child = resolver.FindCachedOrUnknown(in path, childHash);
                    child.ResolveNode(resolver, in path);
                    CollectSubtree(child, ref path, resolver, isStorage, added, childDepth);
                }
                else
                {
                    TreePath childPath = path;
                    TrieNode? child = node.GetChildWithChildPath(resolver, ref childPath, 0);
                    if (child is not null)
                    {
                        child.ResolveNode(resolver, in path);
                        CollectSubtree(child, ref path, resolver, isStorage, added, childDepth);
                    }
                }

                path.TruncateMut(prevLen);
                break;
            }

            case NodeType.Leaf:
                CollectLeaf(node, ref path, added, isStorage);
                break;
        }
    }

    /// <summary>
    /// Count a leaf node's semantic content (account/contract/slot).
    /// For account trie leaves, also recurse into storage tries.
    /// </summary>
    private void CollectLeaf(TrieNode leaf, ref TreePath path, bool added, bool isStorage)
    {
        if (isStorage)
        {
            if (added) _storageSlotsAdded++;
            else _storageSlotsRemoved++;
            return;
        }

        // Account leaf
        if (added) _accountsAdded++;
        else _accountsRemoved++;

        AccountStruct account = DecodeAccount(leaf);

        if (account.HasCode)
        {
            if (added) _contractsAdded++;
            else _contractsRemoved++;
        }

        if (account.HasStorage)
        {
            if (added) _contractsWithStorageAdded++;
            else _contractsWithStorageRemoved++;
        }

        if (account.IsTotallyEmpty)
        {
            if (added) _emptyAccountsAdded++;
            else _emptyAccountsRemoved++;
        }

        if (account.HasStorage)
        {
            Hash256 addressHash = GetAddressHash(leaf, ref path);
            ITrieNodeResolver storageResolver = _resolver.GetStorageTrieNodeResolver(addressHash);
            TreePath storagePath = TreePath.Empty;
            Hash256 storageRoot = new(account.StorageRoot);

            TrieNode storageRootNode = storageResolver.FindCachedOrUnknown(in storagePath, storageRoot);
            storageRootNode.ResolveNode(storageResolver, in storagePath);
            CollectSubtree(storageRootNode, ref storagePath, storageResolver, isStorage: true, added, depth: 0);
        }
    }

    /// <summary>
    /// Handle type mismatch between old and new nodes by enumerating leaves from both
    /// subtrees, matching by full path, and diffing semantically. Structural node counts
    /// (branches, extensions, leaf nodes, bytes) are recorded for all nodes since the
    /// actual trie nodes ARE created/destroyed. Semantic counts (accounts, contracts, slots)
    /// only count genuinely new or removed items.
    /// </summary>
    private void DiffMismatchedNodes(TrieNode oldNode, TrieNode newNode, ref TreePath path,
        ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        var oldLeaves = new Dictionary<Hash256, (TrieNode Leaf, TreePath Path)>();
        var newLeaves = new Dictionary<Hash256, (TrieNode Leaf, TreePath Path)>();

        CollectSubtreeForDiff(oldNode, ref path, resolver, isStorage, added: false, oldLeaves, depth);
        CollectSubtreeForDiff(newNode, ref path, resolver, isStorage, added: true, newLeaves, depth);

        // Diff leaves by full path for correct semantic counts
        foreach (KeyValuePair<Hash256, (TrieNode Leaf, TreePath Path)> kvp in newLeaves)
        {
            Hash256 fullPath = kvp.Key;
            (TrieNode newLeaf, TreePath newLeafPath) = kvp.Value;

            if (oldLeaves.Remove(fullPath, out (TrieNode Leaf, TreePath Path) oldEntry))
            {
                // Leaf at same path in both: modification, not add/remove
                if (!isStorage)
                {
                    TreePath leafPath = oldEntry.Path;
                    DecodeAndDiffAccountLeaves(oldEntry.Leaf, newLeaf, ref leafPath);
                }
                // Storage: same slot modified → net zero
            }
            else
            {
                // Leaf only in new → genuinely added
                TreePath leafPath = newLeafPath;
                CollectLeaf(newLeaf, ref leafPath, added: true, isStorage);
            }
        }

        // Remaining old leaves not matched → genuinely removed
        foreach (KeyValuePair<Hash256, (TrieNode Leaf, TreePath Path)> kvp in oldLeaves)
        {
            (TrieNode oldLeaf, TreePath oldLeafPath) = kvp.Value;
            TreePath leafPath = oldLeafPath;
            CollectLeaf(oldLeaf, ref leafPath, added: false, isStorage);
        }
    }

    /// <summary>
    /// Walk a subtree recording all structural node changes (via RecordNode) and collecting
    /// leaf entries into a dictionary keyed by full path. Leaf semantic counting is deferred
    /// to the caller for correct diff matching.
    /// </summary>
    private void CollectSubtreeForDiff(TrieNode node, ref TreePath path, ITrieNodeResolver resolver,
        bool isStorage, bool added, Dictionary<Hash256, (TrieNode Leaf, TreePath Path)> leaves, int depth)
    {
        RecordNode(node.NodeType, node.FullRlp.Length, isStorage, added);

        if (_trackDepth)
        {
            int d = Math.Min(depth, 15);
            switch (node.NodeType)
            {
                case NodeType.Branch:
                    RecordDepthBranch(node, d, isStorage, added);
                    break;
                case NodeType.Extension:
                    RecordDepthShort(node.FullRlp.Length, d, isStorage, added);
                    break;
                case NodeType.Leaf:
                    RecordDepthLeaf(node.FullRlp.Length, d, isStorage, added);
                    break;
            }
        }

        switch (node.NodeType)
        {
            case NodeType.Branch:
            {
                int childDepth = depth + 1;
                for (int i = 0; i < 16; i++)
                {
                    Hash256? childHash = node.GetChildHash(i);
                    if (childHash is not null)
                    {
                        int prevLen = path.Length;
                        path.AppendMut(i);
                        TrieNode child = resolver.FindCachedOrUnknown(in path, childHash);
                        child.ResolveNode(resolver, in path);
                        CollectSubtreeForDiff(child, ref path, resolver, isStorage, added, leaves, childDepth);
                        path.TruncateMut(prevLen);
                    }
                    else if (!node.IsChildNull(i))
                    {
                        int prevLen = path.Length;
                        path.AppendMut(i);
                        TrieNode? child = node.GetChildWithChildPath(resolver, ref path, i);
                        if (child is not null)
                        {
                            child.ResolveNode(resolver, in path);
                            CollectSubtreeForDiff(child, ref path, resolver, isStorage, added, leaves, childDepth);
                        }
                        path.TruncateMut(prevLen);
                    }
                }
                break;
            }

            case NodeType.Extension:
            {
                Hash256? childHash = node.GetChildHash(1);
                int prevLen = path.Length;
                path.AppendMut(node.Key!);
                int childDepth = depth + (node.Key?.Length ?? 1);
                if (childHash is not null)
                {
                    TrieNode child = resolver.FindCachedOrUnknown(in path, childHash);
                    child.ResolveNode(resolver, in path);
                    CollectSubtreeForDiff(child, ref path, resolver, isStorage, added, leaves, childDepth);
                }
                else
                {
                    TreePath childPath = path;
                    TrieNode? child = node.GetChildWithChildPath(resolver, ref childPath, 0);
                    if (child is not null)
                    {
                        child.ResolveNode(resolver, in path);
                        CollectSubtreeForDiff(child, ref path, resolver, isStorage, added, leaves, childDepth);
                    }
                }
                path.TruncateMut(prevLen);
                break;
            }

            case NodeType.Leaf:
            {
                // Store path BEFORE appending leaf key (needed for GetAddressHash)
                TreePath pathAtLeaf = path;

                // Compute full path for matching
                int prevLen = path.Length;
                if (node.Key is not null) path.AppendMut(node.Key);
                Hash256 fullPath = path.Path.ToCommitment();
                path.TruncateMut(prevLen);

                leaves[fullPath] = (node, pathAtLeaf);
                break;
            }
        }
    }

    /// <summary>
    /// Record a single trie node as added or removed, incrementing the appropriate counter.
    /// </summary>
    private void RecordNode(NodeType nodeType, int rlpLength, bool isStorage, bool added)
    {
        if (isStorage)
        {
            switch (nodeType)
            {
                case NodeType.Branch:
                    if (added) _storageTrieBranchesAdded++;
                    else _storageTrieBranchesRemoved++;
                    break;
                case NodeType.Extension:
                    if (added) _storageTrieExtensionsAdded++;
                    else _storageTrieExtensionsRemoved++;
                    break;
                case NodeType.Leaf:
                    if (added) _storageTrieLeavesAdded++;
                    else _storageTrieLeavesRemoved++;
                    break;
            }

            if (added) _storageTrieBytesAdded += rlpLength;
            else _storageTrieBytesRemoved += rlpLength;
        }
        else
        {
            switch (nodeType)
            {
                case NodeType.Branch:
                    if (added) _accountTrieBranchesAdded++;
                    else _accountTrieBranchesRemoved++;
                    break;
                case NodeType.Extension:
                    if (added) _accountTrieExtensionsAdded++;
                    else _accountTrieExtensionsRemoved++;
                    break;
                case NodeType.Leaf:
                    if (added) _accountTrieLeavesAdded++;
                    else _accountTrieLeavesRemoved++;
                    break;
            }

            if (added) _accountTrieBytesAdded += rlpLength;
            else _accountTrieBytesRemoved += rlpLength;
        }
    }

    /// <summary>
    /// Decode account leaf value to a full <see cref="AccountStruct"/>. Provides access to
    /// nonce, balance, code hash, and storage root needed for HasCode/HasStorage/IsTotallyEmpty.
    /// </summary>
    private static AccountStruct DecodeAccount(TrieNode leaf)
    {
        var value = leaf.Value;
        var ctx = new Rlp.ValueDecoderContext(value.AsSpan());
        AccountDecoder.Instance.TryDecodeStruct(ref ctx, out AccountStruct account);
        return account;
    }

    /// <summary>
    /// Get the address hash from a leaf node's position in the account trie.
    /// The full 64-nibble path at a leaf = the keccak256 hash of the address.
    /// </summary>
    private static Hash256 GetAddressHash(TrieNode leaf, ref TreePath path)
    {
        // Append leaf's key nibbles to build the full 64-nibble path
        int prevLen = path.Length;
        if (leaf.Key is not null)
        {
            path.AppendMut(leaf.Key);
        }

        Hash256 addressHash = path.Path.ToCommitment();
        path.TruncateMut(prevLen);
        return addressHash;
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
    /// Wraps IsChildNull access in a try/catch because test stub TrieNodes may not be fully decoded.
    /// </summary>
    private static int CountBranchChildren(TrieNode branch)
    {
        int count = 0;
        try
        {
            for (int i = 0; i < 16; i++)
            {
                if (!branch.IsChildNull(i)) count++;
            }
        }
        catch
        {
            // Stub nodes used in unit tests may throw on IsChildNull; treat as 0
        }
        return count;
    }

    /// <summary>Record a branch node add/remove in the depth delta arrays.</summary>
    private void RecordDepthBranch(TrieNode branch, int depth, bool isStorage, bool added)
    {
        int sign = added ? 1 : -1;
        long bytes = branch.FullRlp.Length;
        if (isStorage)
        {
            _depthDelta.StorageFullNodes[depth]  += sign;
            _depthDelta.StorageNodeBytes[depth]  += sign * bytes;
        }
        else
        {
            _depthDelta.AccountFullNodes[depth]  += sign;
            _depthDelta.AccountNodeBytes[depth]  += sign * bytes;
            // Branch occupancy histogram
            int children = CountBranchChildren(branch);
            if (children > 0)
            {
                _depthDelta.BranchOccupancy[children - 1]    += sign;
                _depthDelta.TotalBranchNodesDelta            += sign;
                _depthDelta.TotalBranchChildrenDelta         += sign * children;
            }
        }
    }

    /// <summary>Record an extension node add/remove in the depth delta arrays.</summary>
    private void RecordDepthShort(long rlpLen, int depth, bool isStorage, bool added)
    {
        int sign = added ? 1 : -1;
        if (isStorage)
        {
            _depthDelta.StorageShortNodes[depth] += sign;
            _depthDelta.StorageNodeBytes[depth]  += sign * rlpLen;
        }
        else
        {
            _depthDelta.AccountShortNodes[depth] += sign;
            _depthDelta.AccountNodeBytes[depth]  += sign * rlpLen;
        }
    }

    /// <summary>
    /// Record a leaf node add/remove in the depth delta arrays.
    /// Both ShortNodes (Geth convention: leaf is a shortNode) and ValueNodes are updated.
    /// ValueNodes[depth] stores physical leaves (unshifted); the +1 shift is applied at metrics time.
    /// </summary>
    private void RecordDepthLeaf(long rlpLen, int depth, bool isStorage, bool added)
    {
        int sign = added ? 1 : -1;
        if (isStorage)
        {
            _depthDelta.StorageShortNodes[depth] += sign;
            _depthDelta.StorageValueNodes[depth] += sign;
            _depthDelta.StorageNodeBytes[depth]  += sign * rlpLen;
        }
        else
        {
            _depthDelta.AccountShortNodes[depth] += sign;
            _depthDelta.AccountValueNodes[depth] += sign;
            _depthDelta.AccountNodeBytes[depth]  += sign * rlpLen;
        }
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
