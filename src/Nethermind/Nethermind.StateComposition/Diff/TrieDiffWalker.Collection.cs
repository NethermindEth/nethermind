// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.StateComposition.Diff;

internal sealed partial class TrieDiffWalker
{
    /// <summary>
    /// Recursively collect all nodes in a subtree as either added or removed.
    /// Also counts accounts, contracts, and storage slots at leaves.
    /// </summary>
    private void CollectSubtree(TrieNode node, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, bool added, int depth)
    {
        RecordNode(node.NodeType, node.FullRlp.Length, isStorage, added);

        if (trackDepth)
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
            ITrieNodeResolver storageResolver = resolver.GetStorageTrieNodeResolver(addressHash);
            TreePath storagePath = TreePath.Empty;
            Hash256 storageRoot = new(account.StorageRoot);

            TrieNode storageRootNode = storageResolver.FindCachedOrUnknown(in storagePath, storageRoot);
            storageRootNode.ResolveNode(storageResolver, in storagePath);
            CollectSubtree(storageRootNode, ref storagePath, storageResolver, isStorage: true, added, depth: 0);
        }
    }

    /// <summary>
    /// Walk a subtree recording all structural node changes (via RecordNode) and collecting
    /// leaf entries into a dictionary keyed by full path. Leaf semantic counting is deferred
    /// to the caller for correct diff matching.
    /// </summary>
    private void CollectSubtreeForDiff(TrieNode node, ref TreePath path, ITrieNodeResolver resolver,
        bool isStorage, bool added, Dictionary<ValueHash256, (TrieNode Leaf, TreePath Path)> leaves, int depth)
    {
        RecordNode(node.NodeType, node.FullRlp.Length, isStorage, added);

        if (trackDepth)
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

                    // Compute full path for matching — use ValueHash256 directly to avoid allocation
                    int prevLen = path.Length;
                    if (node.Key is not null) path.AppendMut(node.Key);
                    ValueHash256 fullPath = path.Path;
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
}
