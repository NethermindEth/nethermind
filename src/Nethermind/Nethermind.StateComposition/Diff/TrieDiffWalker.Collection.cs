// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Diff;

internal sealed partial class TrieDiffWalker
{
    private interface ILeafHandler
    {
        void Handle(TrieNode leaf, ref TreePath path, bool added, bool isStorage);
    }

    private struct SemanticLeafHandler(TrieDiffWalker walker) : ILeafHandler
    {
        public void Handle(TrieNode leaf, ref TreePath path, bool added, bool isStorage)
            => walker.CollectLeaf(leaf, ref path, added, isStorage);
    }

    private struct DictionaryLeafHandler(Dictionary<ValueHash256, (TrieNode Leaf, TreePath Path)> leaves) : ILeafHandler
    {
        public void Handle(TrieNode leaf, ref TreePath path, bool added, bool isStorage)
        {
            TreePath pathAtLeaf = path;
            int prevLen = path.Length;
            if (leaf.Key is not null) path.AppendMut(leaf.Key);
            ValueHash256 fullPath = path.Path;
            path.TruncateMut(prevLen);
            leaves[fullPath] = (leaf, pathAtLeaf);
        }
    }

    private void WalkStructure<TH>(TrieNode node, ref TreePath path, ITrieNodeResolver resolver,
        bool isStorage, bool added, int depth, ref TH leafHandler)
        where TH : struct, ILeafHandler
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
                            WalkStructure(child, ref path, resolver, isStorage, added, childDepth, ref leafHandler);
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
                                WalkStructure(child, ref path, resolver, isStorage, added, childDepth, ref leafHandler);
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
                    // Structural depth — see TrieDiffWalker.Extensions.DiffExtensions for rationale.
                    int childDepth = depth + 1;

                    if (childHash is not null)
                    {
                        TrieNode child = resolver.FindCachedOrUnknown(in path, childHash);
                        child.ResolveNode(resolver, in path);
                        WalkStructure(child, ref path, resolver, isStorage, added, childDepth, ref leafHandler);
                    }
                    else
                    {
                        TreePath childPath = path;
                        TrieNode? child = node.GetChildWithChildPath(resolver, ref childPath, 0);
                        if (child is not null)
                        {
                            child.ResolveNode(resolver, in path);
                            WalkStructure(child, ref path, resolver, isStorage, added, childDepth, ref leafHandler);
                        }
                    }

                    path.TruncateMut(prevLen);
                    break;
                }

            case NodeType.Leaf:
                leafHandler.Handle(node, ref path, added, isStorage);
                break;
        }
    }

    private void CollectSubtree(TrieNode node, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, bool added, int depth)
    {
        SemanticLeafHandler handler = new(this);
        WalkStructure(node, ref path, resolver, isStorage, added, depth, ref handler);
    }

    private void CollectSubtreeForDiff(TrieNode node, ref TreePath path, ITrieNodeResolver resolver,
        bool isStorage, bool added, Dictionary<ValueHash256, (TrieNode Leaf, TreePath Path)> leaves, int depth)
    {
        DictionaryLeafHandler handler = new(leaves);
        WalkStructure(node, ref path, resolver, isStorage, added, depth, ref handler);
    }

    private void CollectLeaf(TrieNode leaf, ref TreePath path, bool added, bool isStorage)
    {
        if (isStorage)
        {
            if (added)
            {
                _storageSlotsAdded++;
                if (_inContractStorage) _currentContractSlotDelta++;
            }
            else
            {
                _storageSlotsRemoved++;
                if (_inContractStorage) _currentContractSlotDelta--;
            }
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

        // Whole-account create or delete: emit a code-hash transition between NoCode
        // and the account's code hash so the incremental tracker can refcount correctly.
        // DecodeAndDiffAccountLeaves handles the matched-leaf path separately.
        if (account.HasCode || account.HasStorage)
        {
            Hash256 addressHash = GetAddressHash(leaf, ref path);

            if (account.HasCode)
            {
                ValueHash256 oldCh = added ? CodeHashChange.NoCode : account.CodeHash;
                ValueHash256 newCh = added ? account.CodeHash : CodeHashChange.NoCode;
                RecordCodeHashChange(addressHash.ValueHash256, oldCh, newCh);
            }

            if (account.HasStorage)
            {
                ITrieNodeResolver storageResolver = rootResolver.GetStorageTrieNodeResolver(addressHash);
                TreePath storagePath = TreePath.Empty;
                Hash256 storageRoot = new(account.StorageRoot);

                TrieNode storageRootNode = storageResolver.FindCachedOrUnknown(in storagePath, storageRoot);
                storageRootNode.ResolveNode(storageResolver, in storagePath);

                BeginContractStorage(addressHash.ValueHash256);
                try
                {
                    CollectSubtree(storageRootNode, ref storagePath, storageResolver, isStorage: true, added, depth: 0);
                }
                finally
                {
                    EndContractStorage();
                }
            }
        }
    }

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
