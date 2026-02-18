// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync
{
    public static class SnapProviderHelper
    {
        private const int ExtensionRlpChildIndex = 1;

        public static (AddRangeResult result, bool moreChildrenToRight, List<PathWithAccount> storageRoots, List<ValueHash256> codeHashes, Hash256 actualRootHash) AddAccountRange(
            ISnapTrieFactory factory,
            long blockNumber,
            in ValueHash256 expectedRootHash,
            in ValueHash256 startingHash,
            in ValueHash256 limitHash,
            IReadOnlyList<PathWithAccount> accounts,
            IReadOnlyList<byte[]> proofs = null
        )
        {
            using ISnapTree tree = factory.CreateStateTree();

            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(accounts.Count);
            for (int index = 0; index < accounts.Count; index++)
            {
                PathWithAccount account = accounts[index];
                Account accountValue = account.Account;
                Rlp rlp = accountValue.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Rlp.Encode(accountValue);
                entries.Add(new PatriciaTree.BulkSetEntry(account.Path, rlp.Bytes));
                Interlocked.Add(ref Metrics.SnapStateSynced, rlp.Bytes.Length);
            }

            (AddRangeResult result, bool moreChildrenToRight, _) = CommitRange(
                tree, entries, startingHash, limitHash, expectedRootHash, proofs);
            if (result != AddRangeResult.OK)
                return (result, true, null, null, tree.RootHash);

            List<PathWithAccount> accountsWithStorage = new();
            List<ValueHash256> codeHashes = new();
            for (int index = 0; index < accounts.Count; index++)
            {
                PathWithAccount account = accounts[index];

                if (account.Account.HasStorage && account.Path <= limitHash)
                    accountsWithStorage.Add(account);

                if (account.Account.HasCode)
                    codeHashes.Add(account.Account.CodeHash);
            }

            return (AddRangeResult.OK, moreChildrenToRight, accountsWithStorage, codeHashes, tree.RootHash);
        }

        public static (AddRangeResult result, bool moreChildrenToRight, Hash256 actualRootHash, bool isRootPersisted) AddStorageRange(
            ISnapTrieFactory factory,
            PathWithAccount account,
            IReadOnlyList<PathWithStorageSlot> slots,
            in ValueHash256? startingHash,
            in ValueHash256? limitHash,
            IReadOnlyList<byte[]>? proofs = null
        )
        {
            using ISnapTree tree = factory.CreateStorageTree(account.Path);

            ValueHash256 effectiveLimitHash = limitHash ?? Keccak.MaxValue;
            ValueHash256 effectiveStartingHash = startingHash ?? ValueKeccak.Zero;

            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(slots.Count);
            for (int index = 0; index < slots.Count; index++)
            {
                PathWithStorageSlot slot = slots[index];

                Interlocked.Add(ref Metrics.SnapStateSynced, slot.SlotRlpValue.Length);
                entries.Add(new PatriciaTree.BulkSetEntry(slot.Path, slot.SlotRlpValue));
            }

            (AddRangeResult result, bool moreChildrenToRight, bool isRootPersisted) = CommitRange(
                tree, entries, effectiveStartingHash, effectiveLimitHash, account.Account.StorageRoot, proofs);
            if (result != AddRangeResult.OK)
                return (result, true, tree.RootHash, false);
            return (AddRangeResult.OK, moreChildrenToRight, tree.RootHash, isRootPersisted);
        }

        private static (AddRangeResult result, bool moreChildrenToRight, bool isRootPersisted) CommitRange(
            ISnapTree tree,
            in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries,
            in ValueHash256 startingHash,
            in ValueHash256 limitHash,
            in ValueHash256 expectedRootHash,
            IReadOnlyList<byte[]>? proofs)
        {
            if (entries.Count == 0)
                return (AddRangeResult.EmptyRange, true, false);

            // Validate sorting order
            for (int i = 1; i < entries.Count; i++)
            {
                if (entries[i - 1].Path.CompareTo(entries[i].Path) >= 0)
                    return (AddRangeResult.InvalidOrder, true, false);
            }

            if (entries[0].Path < startingHash)
                return (AddRangeResult.InvalidOrder, true, false);

            ValueHash256 lastPath = entries[entries.Count - 1].Path;

            (AddRangeResult result, List<(TrieNode, TreePath)> sortedBoundaryList, bool moreChildrenToRight) =
                FillBoundaryTree(tree, startingHash, lastPath, limitHash, expectedRootHash, proofs);

            if (result != AddRangeResult.OK)
                return (result, true, false);

            tree.BulkSetAndUpdateRootHash(entries);

            if (tree.RootHash.ValueHash256 != expectedRootHash)
                return (AddRangeResult.DifferentRootHash, true, false);

            StitchBoundaries(sortedBoundaryList, tree, startingHash);

            // The upper bound is used to prevent proof nodes that covers next range from being persisted, except if
            // this is the last range. This prevent double node writes per path which break flat. It also prevent leaf o
            // that is after the range from being persisted, which prevent double write again.
            ValueHash256 upperBound = lastPath;
            if (upperBound > limitHash)
            {
                upperBound = limitHash;
            }
            else
            {
                if (!moreChildrenToRight) upperBound = ValueKeccak.MaxValue;
            }
            tree.Commit(upperBound);

            bool isRootPersisted = sortedBoundaryList is not { Count: > 0 } || sortedBoundaryList[0].Item1.IsPersisted;
            return (AddRangeResult.OK, moreChildrenToRight, isRootPersisted);
        }

        [SkipLocalsInit]
        private static (AddRangeResult result, List<(TrieNode, TreePath)> sortedBoundaryList, bool moreChildrenToRight) FillBoundaryTree(
            ISnapTree tree,
            in ValueHash256? startingHash,
            in ValueHash256 endHash,
            in ValueHash256 limitHash,
            in ValueHash256 expectedRootHash,
            IReadOnlyList<byte[]>? proofs = null
        )
        {
            if (proofs is null || proofs.Count == 0)
            {
                return (AddRangeResult.OK, null, false);
            }

            ArgumentNullException.ThrowIfNull(tree);

            ValueHash256 effectiveStartingHash = startingHash ?? ValueKeccak.Zero;
            List<(TrieNode, TreePath)> sortedBoundaryList = new();

            Dictionary<ValueHash256, TrieNode> dict = CreateProofDict(proofs);

            if (!dict.TryGetValue(expectedRootHash, out TrieNode root))
            {
                return (AddRangeResult.MissingRootHashInProofs, null, true);
            }

            if (dict.Count == 1 && root.IsLeaf)
            {
                // Special case with some server sending proof where the root is the same as the only path.
                // Without this the proof's IsBoundaryNode flag will cause the key to not get saved.
                TreePath rootPath = TreePath.FromNibble(root.Key);
                if (rootPath.Length == 64 && rootPath.Path.Equals(endHash))
                {
                    return (AddRangeResult.OK, null, false);
                }
            }

            TreePath leftBoundaryPath = TreePath.FromPath(effectiveStartingHash.Bytes);
            TreePath rightBoundaryPath = TreePath.FromPath(endHash.Bytes);

            // For when in very-very unlikely case where the last remaining address is Keccak.MaxValue, (who knows why,
            // the chain have special handling for it maybe) and it is not included the returned account range, (again,
            // very-very unlikely), we want `moreChildrenToRight` to return true.
            bool noLimit = limitHash == ValueKeccak.MaxValue;

            // Connect the proof nodes starting from state root.
            // It also remove child path which is within the start/end range. If key are missing, the resolved
            // hash will not match.
            Stack<(TrieNode node, TreePath path)> proofNodesToProcess = new();

            tree.SetRootFromProof(root);
            proofNodesToProcess.Push((root, TreePath.Empty));
            sortedBoundaryList.Add((root, TreePath.Empty));

            bool moreChildrenToRight = false;
            while (proofNodesToProcess.Count > 0)
            {
                (TrieNode node, TreePath path) = proofNodesToProcess.Pop();

                if (node.IsExtension)
                {
                    if (node.GetChildHashAsValueKeccak(ExtensionRlpChildIndex, out ValueHash256 childKeccak))
                    {
                        TreePath childPath = path.Append(node.Key);

                        moreChildrenToRight |= childPath.Path > limitHash;

                        if (dict.TryGetValue(childKeccak, out TrieNode child))
                        {
                            node.SetChild(0, child);

                            proofNodesToProcess.Push((child, childPath));
                            sortedBoundaryList.Add((child, childPath));
                        }
                    }
                }
                else if (node.IsBranch)
                {
                    int left = leftBoundaryPath.CompareToTruncated(path, path.Length) == 0 ? leftBoundaryPath[path.Length] : 0;
                    int right = rightBoundaryPath.CompareToTruncated(path, path.Length) == 0 ? rightBoundaryPath[path.Length] : 15;

                    int maxIndex = moreChildrenToRight ? right : 15;

                    for (int ci = left; ci <= maxIndex; ci++)
                    {
                        bool hasKeccak = node.GetChildHashAsValueKeccak(ci, out ValueHash256 childKeccak);
                        TrieNode? child = null;
                        if (hasKeccak)
                        {
                            dict.TryGetValue(childKeccak, out child);
                        }

                        if (child is null)
                        {
                            // Note: be careful with inline node. Inline node is not set in the proof dictionary
                            byte[]? inlineRlp = node.GetInlineNodeRlp(ci);
                            if (inlineRlp is not null)
                            {
                                child = new TrieNode(NodeType.Unknown, inlineRlp);
                                child.ResolveNode(NullTrieNodeResolver.Instance, path.Append(ci));
                            }
                        }

                        // The limit may have lower nibble that is less than the path's current nibble, even if upper
                        // nibble is higher. So need to check whole path
                        TreePath childPath = path.Append(ci);
                        moreChildrenToRight |= (hasKeccak || child is not null) && (ci > right && (childPath.Path <= limitHash || noLimit));

                        if (ci >= left && ci <= right)
                        {
                            // Clear child within boundary
                            node.SetChild(ci, null);
                        }

                        if (child is not null && !hasKeccak && (ci == left || ci == right))
                        {
                            // Inline node at boundary. Need to be set back or keccak will be incorrect.
                            // but must not be set as part of boundary list or break stitching.
                            TreePath wholePath = childPath.Append(child.Key);
                            if (leftBoundaryPath.CompareToTruncated(wholePath, wholePath.Length) > 0 || rightBoundaryPath.CompareToTruncated(wholePath, wholePath.Length) < 0)
                            {
                                node.SetChild(ci, child);
                            }
                        }

                        if (hasKeccak && (ci == left || ci == right) && child is not null)
                        {
                            if (child.IsBranch)
                            {
                                node.SetChild(ci, child);

                                proofNodesToProcess.Push((child, childPath));
                                sortedBoundaryList.Add((child, childPath));
                            }
                            else if (child.IsExtension)
                            {
                                // If its an extension, its path + key must be outside or equal to the boundary.
                                TreePath wholePath = childPath.Append(child.Key);
                                if (leftBoundaryPath.CompareToTruncated(wholePath, wholePath.Length) >= 0 || rightBoundaryPath.CompareToTruncated(wholePath, wholePath.Length) <= 0)
                                {
                                    node.SetChild(ci, child);
                                    proofNodesToProcess.Push((child, childPath));
                                    sortedBoundaryList.Add((child, childPath));
                                }
                            }
                            else
                            {
                                // If its a leaf, its path + key must be outside the boundary.
                                TreePath wholePath = childPath.Append(child.Key);
                                if (leftBoundaryPath.CompareToTruncated(wholePath, wholePath.Length) > 0 || rightBoundaryPath.CompareToTruncated(wholePath, wholePath.Length) < 0)
                                {
                                    node.SetChild(ci, child);
                                    proofNodesToProcess.Push((child, childPath));
                                    sortedBoundaryList.Add((child, childPath));
                                }
                            }
                        }
                    }
                }
            }

            return (AddRangeResult.OK, sortedBoundaryList, moreChildrenToRight);
        }

        private static Dictionary<ValueHash256, TrieNode> CreateProofDict(IReadOnlyList<byte[]> proofs)
        {
            Dictionary<ValueHash256, TrieNode> dict = new();

            for (int i = 0; i < proofs.Count; i++)
            {
                byte[] proof = proofs[i];
                TrieNode node = new(NodeType.Unknown, proof, isDirty: true);
                node.IsBoundaryProofNode = true;

                TreePath emptyPath = TreePath.Empty;
                node.ResolveNode(UnknownNodeResolver.Instance, emptyPath);
                node.ResolveKey(UnknownNodeResolver.Instance, ref emptyPath);

                dict[node.Keccak] = node;
            }

            return dict;
        }

        private static void StitchBoundaries(List<(TrieNode, TreePath)>? sortedBoundaryList, ISnapTree tree, ValueHash256 startPath)
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
                        if (IsChildPersisted(node, ref path, extensionData._value, ExtensionRlpChildIndex, tree, startPath))
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
                            if (!IsChildPersisted(node, ref path, o, ci, tree, startPath))
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

        private static bool IsChildPersisted(TrieNode node, ref TreePath nodePath, object? child, int childIndex, ISnapTree tree, ValueHash256 startPath)
        {
            if (child is TrieNode childNode)
            {
                /*
                if (childNode.FullRlp.Length < 32)
                {
                    TreePath childPath = nodePath.Append(childIndex);
                    TreePath fullPath = childPath.Append(childNode.Key);
                    if (fullPath.Path < startPath)
                    {
                        // When a branch have an inline leaf whose full path is < startPath,
                        // we cannot mark it as persisted and cause the branch proof to be persisted. This is because
                        // we dont know if the inline leaf is part of a different storage root or not.
                        return false;
                    }
                }
                */
                return childNode.IsBoundaryProofNode == false;
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
}
