// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.StateDiff.Core.Data;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.StateDiff.Core.Diff;

public sealed partial class TrieDiffWalker
{
    private interface ILeafHandler
    {
        void Handle(TrieNode leaf, ref TreePath path, bool added, bool isStorage, ResolverPair resolvers);
    }

    private struct SemanticLeafHandler(TrieDiffWalker walker) : ILeafHandler
    {
        public readonly void Handle(TrieNode leaf, ref TreePath path, bool added, bool isStorage, ResolverPair resolvers)
            => walker.CollectLeaf(leaf, ref path, added, isStorage, resolvers);
    }

    private struct DictionaryLeafHandler(Dictionary<ValueHash256, (TrieNode Leaf, TreePath Path)> leaves) : ILeafHandler
    {
        public readonly void Handle(TrieNode leaf, ref TreePath path, bool added, bool isStorage, ResolverPair resolvers)
        {
            TreePath pathAtLeaf = path;
            int prevLen = path.Length;
            if (leaf.Key is not null) path.AppendMut(leaf.Key);
            ValueHash256 fullPath = path.Path;
            path.TruncateMut(prevLen);
            leaves[fullPath] = (leaf, pathAtLeaf);
        }
    }

    private void WalkStructure<TH>(TrieNode node, ref TreePath path, ResolverPair resolvers,
        bool isStorage, bool added, ref TH leafHandler)
        where TH : struct, ILeafHandler
    {
        ITrieNodeResolver side = resolvers.Pick(added);

        // Every visited node contributes its full RLP length to the per-CF byte
        // delta — matches the legacy StateComposition walker's RecordNode call
        // site at the top of WalkStructure so the net (added - removed) numbers
        // stay equal between the two walkers during the parallel-validation soak.
        RecordNodeBytes(node.FullRlp.Length, isStorage, added);

        switch (node.NodeType)
        {
            case NodeType.Branch:
                {
                    for (int i = 0; i < 16; i++)
                    {
                        Hash256? childHash = node.GetChildHash(i);

                        if (childHash is not null)
                        {
                            int prevLen = path.Length;
                            path.AppendMut(i);
                            TrieNode child = side.FindCachedOrUnknown(in path, childHash);
                            child.ResolveNode(side, in path);
                            WalkStructure(child, ref path, resolvers, isStorage, added, ref leafHandler);
                            path.TruncateMut(prevLen);
                        }
                        else if (!node.IsChildNull(i))
                        {
                            int prevLen = path.Length;
                            path.AppendMut(i);
                            TrieNode? child = node.GetChildWithChildPath(side, ref path, i);
                            if (child is null)
                            {
                                // GetChildWithChildPath caches via _nodeData and can return null
                                // asymmetrically between resolvers when an inline child's slot was
                                // previously materialised on one side but wiped by UnresolveChild
                                // on the other. Fall back to GetInlineNodeRlp, which decodes from
                                // the parent's RLP directly and is identical across resolvers.
                                byte[]? inlineRlp = node.GetInlineNodeRlp(i);
                                if (inlineRlp is not null)
                                {
                                    child = new TrieNode(NodeType.Unknown, inlineRlp);
                                }
                            }
                            if (child is not null)
                            {
                                child.ResolveNode(side, in path);
                                WalkStructure(child, ref path, resolvers, isStorage, added, ref leafHandler);
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
                    path.AppendMut(node.Key);

                    if (childHash is not null)
                    {
                        TrieNode child = side.FindCachedOrUnknown(in path, childHash);
                        child.ResolveNode(side, in path);
                        WalkStructure(child, ref path, resolvers, isStorage, added, ref leafHandler);
                    }
                    else
                    {
                        TreePath childPath = path;
                        TrieNode? child = node.GetChildWithChildPath(side, ref childPath, 0);
                        if (child is null)
                        {
                            byte[]? inlineRlp = node.GetInlineNodeRlp(0);
                            if (inlineRlp is not null)
                            {
                                child = new TrieNode(NodeType.Unknown, inlineRlp);
                            }
                        }
                        if (child is not null)
                        {
                            child.ResolveNode(side, in path);
                            WalkStructure(child, ref path, resolvers, isStorage, added, ref leafHandler);
                        }
                    }

                    path.TruncateMut(prevLen);
                    break;
                }

            case NodeType.Leaf:
                leafHandler.Handle(node, ref path, added, isStorage, resolvers);
                break;
        }
    }

    private void CollectSubtree(TrieNode node, ref TreePath path, ResolverPair resolvers, bool isStorage, bool added)
    {
        SemanticLeafHandler handler = new(this);
        WalkStructure(node, ref path, resolvers, isStorage, added, ref handler);
    }

    private void CollectSubtreeForDiff(TrieNode node, ref TreePath path, ResolverPair resolvers,
        bool isStorage, bool added, Dictionary<ValueHash256, (TrieNode Leaf, TreePath Path)> leaves)
    {
        DictionaryLeafHandler handler = new(leaves);
        WalkStructure(node, ref path, resolvers, isStorage, added, ref handler);
    }

    private void CollectLeaf(TrieNode leaf, ref TreePath path, bool added, bool isStorage, ResolverPair resolvers)
    {
        if (isStorage)
        {
            if (_inContractStorage)
            {
                if (added) _currentContractSlotDelta++;
                else _currentContractSlotDelta--;
            }
            return;
        }

        // Account-trie leaf entered or left the trie wholesale. Track net leaf
        // count regardless of whether the payload decodes — a degenerate stub
        // still occupies a leaf slot in the trie.
        _accountsAddedDelta += added ? 1 : -1;

        // Degenerate leaves (length-1 empty stubs) still entered/left the trie, but we cannot
        // derive code-hash or storage classifications from them; skip without emitting.
        if (!TryDecodeAccount(leaf, out AccountStruct account)) return;

        if (!account.HasCode && !account.HasStorage) return;

        // Whole-account create or delete: emit a code-hash transition between NoCode and the
        // account's code hash so the sidecar tracker can refcount correctly.
        // DecodeAndDiffAccountLeaves handles the matched-leaf path separately.
        Hash256? addressHash = GetAddressHash(leaf, ref path);
        if (addressHash is null) return;

        if (account.HasCode)
        {
            ValueHash256 oldCh = added ? CodeHashChange.NoCode : account.CodeHash;
            ValueHash256 newCh = added ? account.CodeHash : CodeHashChange.NoCode;
            RecordCodeHashChange(addressHash.ValueHash256, oldCh, newCh);
        }

        if (account.HasStorage)
        {
            ResolverPair storageResolvers = resolvers.ForStorage(addressHash);
            ITrieNodeResolver storageSide = storageResolvers.Pick(added);
            TreePath storagePath = TreePath.Empty;
            Hash256 storageRoot = new(account.StorageRoot);

            TrieNode storageRootNode = storageSide.FindCachedOrUnknown(in storagePath, storageRoot);
            storageRootNode.ResolveNode(storageSide, in storagePath);

            BeginContractStorage(addressHash.ValueHash256);
            try
            {
                CollectSubtree(storageRootNode, ref storagePath, storageResolvers, isStorage: true, added);
            }
            finally
            {
                EndContractStorage();
            }
        }
    }
}
