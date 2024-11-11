// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
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

                Rlp rlp = tree.Set(account.Path, account.Account);
                if (rlp is not null)
                {
                    Interlocked.Add(ref Metrics.SnapStateSynced, rlp.Bytes.Length);
                }
            }

            tree.UpdateRootHash();

            if (tree.RootHash != expectedRootHash)
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
            long blockNumber,
            in ValueHash256? startingHash,
            IReadOnlyList<PathWithStorageSlot> slots,
            in ValueHash256 expectedRootHash,
            IReadOnlyList<byte[]>? proofs = null
        )
        {
            // TODO: Check the slots boundaries and sorting

            ValueHash256 lastHash = slots[^1].Path;

            (AddRangeResult result, List<(TrieNode, TreePath)> sortedBoundaryList, bool moreChildrenToRight) = FillBoundaryTree(
                tree, startingHash, lastHash, ValueKeccak.MaxValue, expectedRootHash, proofs);

            if (result != AddRangeResult.OK)
            {
                return (result, true);
            }

            for (var index = 0; index < slots.Count; index++)
            {
                PathWithStorageSlot slot = slots[index];
                Interlocked.Add(ref Metrics.SnapStateSynced, slot.SlotRlpValue.Length);
                tree.Set(slot.Path, slot.SlotRlpValue, false);
            }

            tree.UpdateRootHash();

            if (tree.RootHash != expectedRootHash)
            {
                return (AddRangeResult.DifferentRootHash, true);
            }

            StitchBoundaries(sortedBoundaryList, tree.TrieStore);

            tree.Commit(writeFlags: WriteFlags.DisableWAL);

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

            ValueHash256 effectiveStartingHAsh = startingHash.HasValue ? startingHash.Value : ValueKeccak.Zero;
            List<(TrieNode, TreePath)> sortedBoundaryList = new();

            Dictionary<ValueHash256, TrieNode> dict = CreateProofDict(proofs, tree.TrieStore);

            if (!dict.TryGetValue(expectedRootHash, out TrieNode root))
            {
                return (AddRangeResult.MissingRootHashInProofs, null, true);
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
                    if (node.GetChildHashAsValueKeccak(0, out ValueHash256 childKeccak))
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

                if (node.IsBranch)
                {
                    int left = leftBoundaryPath.CompareToTruncated(path, path.Length) == 0 ? leftBoundaryPath[path.Length] : 0;
                    int right = rightBoundaryPath.CompareToTruncated(path, path.Length) == 0 ? rightBoundaryPath[path.Length] : 15;
                    int limit = rightLimitPath.CompareToTruncated(path, path.Length) == 0 ? rightLimitPath[path.Length] : 15;

                    int maxIndex = moreChildrenToRight ? right : 15;

                    for (int ci = left; ci <= maxIndex; ci++)
                    {
                        bool hasKeccak = node.GetChildHashAsValueKeccak(ci, out ValueHash256 childKeccak);

                        moreChildrenToRight |= hasKeccak && (ci > right && (ci < limit || noLimit));

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
                node.ResolveKey(store, ref emptyPath, isRoot: i == 0);

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
                    if (node.IsExtension)
                    {
                        if (IsChildPersisted(node, ref path, 1, store))
                        {
                            node.IsBoundaryProofNode = false;
                        }
                    }

                    if (node.IsBranch)
                    {
                        bool isBoundaryProofNode = false;
                        for (int ci = 0; ci <= 15; ci++)
                        {
                            if (!IsChildPersisted(node, ref path, ci, store))
                            {
                                isBoundaryProofNode = true;
                                break;
                            }
                        }

                        node.IsBoundaryProofNode = isBoundaryProofNode;
                    }
                }
            }
        }

        private static bool IsChildPersisted(TrieNode node, ref TreePath nodePath, int childIndex, IScopedTrieStore store)
        {
            TrieNode data = node.GetData(childIndex) as TrieNode;
            if (data is not null)
            {
                return data.IsBoundaryProofNode == false;
            }

            if (!node.GetChildHashAsValueKeccak(childIndex, out ValueHash256 childKeccak))
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
