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

        public static (AddRangeResult result, bool moreChildrenToRight, List<PathWithAccount> storageRoots, List<ValueHash256> codeHashes) AddAccountRange(
            StateTree tree,
            long blockNumber,
            in ValueHash256 expectedRootHash,
            in ValueHash256 startingHash,
            in ValueHash256 limitHash,
            IReadOnlyList<PathWithAccount> accounts,
            IReadOnlyList<byte[]> proofs = null
        )
        {
            // TODO: Check the accounts boundaries and sorting
            if (accounts.Count == 0)
                throw new ArgumentException("Cannot be empty.", nameof(accounts));

            // Validate sorting order
            for (int i = 1; i < accounts.Count; i++)
            {
                if (accounts[i - 1].Path.CompareTo(accounts[i].Path) >= 0)
                {
                    return (AddRangeResult.InvalidOrder, true, null, null);
                }
            }

            ValueHash256 lastHash = accounts[^1].Path;

            (AddRangeResult result, List<(TrieNode, TreePath)> sortedBoundaryList, bool moreChildrenToRight) =
                FillBoundaryTree(tree, startingHash, lastHash, limitHash, expectedRootHash, proofs);

            if (result != AddRangeResult.OK)
            {
                return (result, true, null, null);
            }

            List<PathWithAccount> accountsWithStorage = new();
            List<ValueHash256> codeHashes = new();
            bool hasExtraStorage = false;

            using ArrayPoolList<PatriciaTree.BulkSetEntry> entries = new ArrayPoolList<PatriciaTree.BulkSetEntry>(accounts.Count);
            for (var index = 0; index < accounts.Count; index++)
            {
                PathWithAccount account = accounts[index];
                if (account.Account.HasStorage)
                {
                    if (account.Path >= limitHash || account.Path < startingHash)
                    {
                        hasExtraStorage = true;
                    }
                    else
                    {
                        accountsWithStorage.Add(account);
                    }
                }

                if (account.Account.HasCode)
                {
                    codeHashes.Add(account.Account.CodeHash);
                }

                var account_ = account.Account;
                Rlp rlp = account_ is null ? null : account_.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Rlp.Encode(account_);
                entries.Add(new PatriciaTree.BulkSetEntry(account.Path, rlp?.Bytes));
                if (account is not null)
                {
                    Interlocked.Add(ref Metrics.SnapStateSynced, rlp.Bytes.Length);
                }
            }

            tree.BulkSet(entries, PatriciaTree.Flags.WasSorted);
            tree.UpdateRootHash();

            if (tree.RootHash.ValueHash256 != expectedRootHash)
            {
                return (AddRangeResult.DifferentRootHash, true, null, null);
            }

            if (hasExtraStorage)
            {
                // The server will always give one node extra after limitpath if it can fit in the response.
                // When we have extra storage, the extra storage must not be re-stored as it may have already been set
                // by another top level partition. If the sync pivot moved and the storage was modified, it must not be saved
                // here along with updated ancestor so that healing can detect that the storage need to be healed.
                //
                // Unfortunately, without introducing large change to the tree, the easiest way to
                // exclude the extra storage is to just rebuild the whole tree and also skip stitching.
                // Fortunately, this should only happen n-1 time where n is the number of top level
                // partition count.

                tree.RootHash = Keccak.EmptyTreeHash;
                for (var index = 0; index < accounts.Count; index++)
                {
                    PathWithAccount account = accounts[index];
                    if (account.Path >= limitHash || account.Path < startingHash) continue;
                    _ = tree.Set(account.Path, account.Account);
                }
            }
            else
            {
                StitchBoundaries(sortedBoundaryList, tree.TrieStore);
            }

            tree.Commit(skipRoot: true, writeFlags: WriteFlags.DisableWAL);

            return (AddRangeResult.OK, moreChildrenToRight, accountsWithStorage, codeHashes);
        }

        public static (AddRangeResult result, bool moreChildrenToRight) AddStorageRange(
            StorageTree tree,
            PathWithAccount account,
            IReadOnlyList<PathWithStorageSlot> slots,
            in ValueHash256? startingHash,
            in ValueHash256? limitHash,
            IReadOnlyList<byte[]>? proofs = null
        )
        {
            if (slots.Count == 0)
                throw new ArgumentException("Cannot be empty.", nameof(slots));

            // Validate sorting order
            for (int i = 1; i < slots.Count; i++)
            {
                if (slots[i - 1].Path.CompareTo(slots[i].Path) >= 0)
                {
                    return (AddRangeResult.InvalidOrder, true);
                }
            }

            ValueHash256 lastHash = slots[^1].Path;

            (AddRangeResult result, List<(TrieNode, TreePath)> sortedBoundaryList, bool moreChildrenToRight) = FillBoundaryTree(
                tree, startingHash, lastHash, limitHash ?? Keccak.MaxValue, account.Account.StorageRoot, proofs);

            if (result != AddRangeResult.OK)
            {
                return (result, true);
            }

            using ArrayPoolList<PatriciaTree.BulkSetEntry> entries = new ArrayPoolList<PatriciaTree.BulkSetEntry>(slots.Count);
            for (var index = 0; index < slots.Count; index++)
            {
                PathWithStorageSlot slot = slots[index];
                Interlocked.Add(ref Metrics.SnapStateSynced, slot.SlotRlpValue.Length);
                entries.Add(new PatriciaTree.BulkSetEntry(slot.Path, slot.SlotRlpValue));
            }

            tree.BulkSet(entries, PatriciaTree.Flags.WasSorted);
            tree.UpdateRootHash();

            if (tree.RootHash.ValueHash256 != account.Account.StorageRoot)
            {
                return (AddRangeResult.DifferentRootHash, true);
            }

            // This will work if all StorageRange requests share the same AccountWithPath object which seems to be the case.
            // If this is not true, StorageRange request should be extended with a lock object.
            // That lock object should be shared between all other StorageRange requests for same account.
            lock (account.Account)
            {
                StitchBoundaries(sortedBoundaryList, tree.TrieStore);
                tree.Commit(writeFlags: WriteFlags.DisableWAL);
            }

            return (AddRangeResult.OK, moreChildrenToRight);
        }

        [SkipLocalsInit]
        private static (AddRangeResult result, List<(TrieNode, TreePath)> sortedBoundaryList, bool moreChildrenToRight) FillBoundaryTree(
            PatriciaTree tree,
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

            ValueHash256 effectiveStartingHAsh = startingHash ?? ValueKeccak.Zero;
            List<(TrieNode, TreePath)> sortedBoundaryList = new();

            Dictionary<ValueHash256, TrieNode> dict = CreateProofDict(proofs, tree.TrieStore);

            if (!dict.TryGetValue(expectedRootHash, out TrieNode root))
            {
                return (AddRangeResult.MissingRootHashInProofs, null, true);
            }

            if (dict.Count == 1 && root.IsLeaf)
            {
                // Special case with some server sending proof where the root is the same as the only path.
                // Without this the proof's IsBoundaryNode flag will cause the key to not get saved.
                var rootPath = TreePath.FromNibble(root.Key);
                if (rootPath.Length == 64 && rootPath.Path.Equals(endHash))
                {
                    return (AddRangeResult.OK, null, false);
                }
            }

            TreePath leftBoundaryPath = TreePath.FromPath(effectiveStartingHAsh.Bytes);
            TreePath rightBoundaryPath = TreePath.FromPath(endHash.Bytes);
            TreePath rightLimitPath = TreePath.FromPath(limitHash.Bytes);

            // For when in very-very unlikely case where the last remaining address is Keccak.MaxValue, (who knows why,
            // the chain have special handling for it maybe) and it is not included the returned account range, (again,
            // very-very unlikely), we want `moreChildrenToRight` to return true.
            bool noLimit = limitHash == ValueKeccak.MaxValue;

            // Connect the proof nodes starting from state root.
            // It also remove child path which is within the start/end range. If key are missing, the resolved
            // hash will not match.
            Stack<(TrieNode node, TreePath path)> proofNodesToProcess = new();

            tree.RootRef = root;
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
                        if (dict.TryGetValue(childKeccak, out TrieNode child))
                        {
                            node.SetChild(0, child);

                            TreePath childPath = path.Append(node.Key);

                            proofNodesToProcess.Push((child, childPath));
                            sortedBoundaryList.Add((child, childPath));
                        }
                    }
                }
                else if (node.IsBranch)
                {
                    int left = leftBoundaryPath.CompareToTruncated(path, path.Length) == 0 ? leftBoundaryPath[path.Length] : 0;
                    int right = rightBoundaryPath.CompareToTruncated(path, path.Length) == 0 ? rightBoundaryPath[path.Length] : 15;
                    int limit = rightLimitPath.CompareToTruncated(path, path.Length) == 0 ? rightLimitPath[path.Length] : 15;

                    int maxIndex = moreChildrenToRight ? right : 15;

                    for (int ci = left; ci <= maxIndex; ci++)
                    {
                        bool hasKeccak = node.GetChildHashAsValueKeccak(ci, out ValueHash256 childKeccak);

                        moreChildrenToRight |= hasKeccak && (ci > right && (ci <= limit || noLimit));

                        if (ci >= left && ci <= right)
                        {
                            // Clear child within boundary
                            node.SetChild(ci, null);
                        }

                        if (hasKeccak && (ci == left || ci == right) && dict.TryGetValue(childKeccak, out TrieNode child))
                        {
                            TreePath childPath = path.Append(ci);

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

        private static Dictionary<ValueHash256, TrieNode> CreateProofDict(IReadOnlyList<byte[]> proofs, IScopedTrieStore store)
        {
            Dictionary<ValueHash256, TrieNode> dict = new();

            for (int i = 0; i < proofs.Count; i++)
            {
                byte[] proof = proofs[i];
                TrieNode node = new(NodeType.Unknown, proof, isDirty: true);
                node.IsBoundaryProofNode = true;

                TreePath emptyPath = TreePath.Empty;
                node.ResolveNode(store, emptyPath);
                node.ResolveKey(store, ref emptyPath);

                dict[node.Keccak] = node;
            }

            return dict;
        }

        private static void StitchBoundaries(List<(TrieNode, TreePath)> sortedBoundaryList, IScopedTrieStore store)
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
                        if (IsChildPersisted(node, ref path, extensionData._value, ExtensionRlpChildIndex, store))
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
                            if (!IsChildPersisted(node, ref path, o, ci, store))
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
                        node.IsPersisted = store.IsPersisted(path, node.Keccak);
                        node.IsBoundaryProofNode = !node.IsPersisted;
                    }
                }
            }
        }

        private static bool IsChildPersisted(TrieNode node, ref TreePath nodePath, object? child, int childIndex, IScopedTrieStore store)
        {
            if (child is TrieNode childNode)
            {
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
                return store.IsPersisted(nodePath, childKeccak);
            }
            finally
            {
                nodePath.TruncateMut(previousPathLength);
            }
        }
    }
}
