// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Stats.Model;
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

            for (var index = 0; index < accounts.Count; index++)
            {
                PathWithAccount account = accounts[index];
                if (account.Account.HasStorage)
                {
                    accountsWithStorage.Add(account);
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

            StitchBoundaries(sortedBoundaryList, tree.TrieStore);

            tree.Commit(blockNumber, skipRoot: true, WriteFlags.DisableWAL);

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

            tree.Commit(blockNumber, writeFlags: WriteFlags.DisableWAL);

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

            // BytesToNibbleBytes will throw if the input is not 32 bytes long, so we can use stackalloc+SkipLocalsInit
            Span<byte> leftBoundary = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(effectiveStartingHAsh.Bytes, leftBoundary);
            Span<byte> rightBoundary = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(endHash.Bytes, rightBoundary);
            Span<byte> rightLimit = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(limitHash.Bytes, rightLimit);

            // For when in very-very unlikely case where the last remaining address is Keccak.MaxValue, (who knows why,
            // the chain have special handling for it maybe) and it is not included the returned account range, (again,
            // very-very unlikely), we want `moreChildrenToRight` to return true.
            bool noLimit = limitHash == ValueKeccak.MaxValue;

            Stack<(TrieNode parent, TrieNode node, int pathIndex, List<byte> path)> proofNodesToProcess = new();

            tree.RootRef = root;
            proofNodesToProcess.Push((null, root, -1, new List<byte>()));
            sortedBoundaryList.Add((root, TreePath.Empty));

            bool moreChildrenToRight = false;

            while (proofNodesToProcess.Count > 0)
            {
                (TrieNode parent, TrieNode node, int pathIndex, List<byte> path) = proofNodesToProcess.Pop();

                if (node.IsExtension)
                {
                    if (node.GetChildHashAsValueKeccak(0, out ValueHash256 childKeccak))
                    {
                        if (dict.TryGetValue(childKeccak, out TrieNode child))
                        {
                            node.SetChild(0, child);

                            pathIndex += node.Key.Length;
                            path.AddRange(node.Key);
                            proofNodesToProcess.Push((node, child, pathIndex, path));
                            sortedBoundaryList.Add((child, TreePath.FromNibble(CollectionsMarshal.AsSpan(path))));
                        }
                        else
                        {
                            Span<byte> pathSpan = CollectionsMarshal.AsSpan(path);
                            if (Bytes.BytesComparer.Compare(pathSpan, leftBoundary[0..path.Count]) >= 0
                                && parent is not null
                                && parent.IsBranch)
                            {
                                for (int i = 0; i < 15; i++)
                                {
                                    if (parent.GetChildHashAsValueKeccak(i, out ValueHash256 kec) && kec == node.Keccak)
                                    {
                                        parent.SetChild(i, null);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                if (node.IsBranch)
                {
                    pathIndex++;

                    Span<byte> pathSpan = CollectionsMarshal.AsSpan(path);
                    int left = Bytes.BytesComparer.Compare(pathSpan, leftBoundary[0..path.Count]) == 0 ? leftBoundary[pathIndex] : 0;
                    int right = Bytes.BytesComparer.Compare(pathSpan, rightBoundary[0..path.Count]) == 0 ? rightBoundary[pathIndex] : 15;
                    int limit = Bytes.BytesComparer.Compare(pathSpan, rightLimit[0..path.Count]) == 0 ? rightLimit[pathIndex] : 15;

                    int maxIndex = moreChildrenToRight ? right : 15;

                    for (int ci = left; ci <= maxIndex; ci++)
                    {
                        bool hasKeccak = node.GetChildHashAsValueKeccak(ci, out ValueHash256 childKeccak);

                        moreChildrenToRight |= hasKeccak && (ci > right && (ci < limit || noLimit));

                        if (ci >= left && ci <= right)
                        {
                            node.SetChild(ci, null);
                        }

                        if (hasKeccak && (ci == left || ci == right) && dict.TryGetValue(childKeccak, out TrieNode child))
                        {
                            if (!child.IsLeaf)
                            {
                                node.SetChild(ci, child);

                                // TODO: we should optimize it - copy only if there are two boundary children
                                List<byte> newPath = new(path)
                                {
                                    (byte)ci
                                };

                                proofNodesToProcess.Push((node, child, pathIndex, newPath));
                                sortedBoundaryList.Add((child, TreePath.FromNibble(CollectionsMarshal.AsSpan(newPath))));
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
