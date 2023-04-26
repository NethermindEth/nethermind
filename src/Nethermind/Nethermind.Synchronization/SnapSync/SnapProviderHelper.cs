// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync
{
    public static class SnapProviderHelper
    {

        public static (AddRangeResult result, bool moreChildrenToRight, IList<PathWithAccount> storageRoots, IList<Keccak> codeHashes) AddAccountRange(
            IStateTree tree,
            long blockNumber,
            Keccak expectedRootHash,
            Keccak startingHash,
            Keccak limitHash,
            PathWithAccount[] accounts,
            byte[][] proofs = null
        )
        {
            // TODO: Check the accounts boundaries and sorting

            Keccak lastHash = accounts[^1].Path;

            (AddRangeResult result, IList<TrieNode> sortedBoundaryList, bool moreChildrenToRight) =
                FillBoundaryTree(tree, startingHash, lastHash, limitHash, expectedRootHash, proofs);

            if (result != AddRangeResult.OK)
            {
                return (result, true, null, null);
            }

            IList<PathWithAccount> accountsWithStorage = new List<PathWithAccount>();
            IList<Keccak> codeHashes = new List<Keccak>();

            for (var index = 0; index < accounts.Length; index++)
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

            tree.Commit(blockNumber, skipRoot: false);

            return (AddRangeResult.OK, moreChildrenToRight, accountsWithStorage, codeHashes);
        }

        public static (AddRangeResult result, bool moreChildrenToRight) AddStorageRange(
            StorageTree tree,
            long blockNumber,
            Keccak? startingHash,
            PathWithStorageSlot[] slots,
            Keccak expectedRootHash,
            byte[][]? proofs = null
        )
        {
            // TODO: Check the slots boundaries and sorting

            Keccak lastHash = slots.Last().Path;

            (AddRangeResult result, IList<TrieNode> sortedBoundaryList, bool moreChildrenToRight) = FillBoundaryTree(
                tree, startingHash, lastHash, Keccak.MaxValue, expectedRootHash, proofs);

            if (result != AddRangeResult.OK)
            {
                return (result, true);
            }

            for (var index = 0; index < slots.Length; index++)
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

            tree.Commit(blockNumber);

            return (AddRangeResult.OK, moreChildrenToRight);
        }

        private static (AddRangeResult result, IList<TrieNode> sortedBoundaryList, bool moreChildrenToRight) FillBoundaryTree(
            IPatriciaTree tree,
            Keccak? startingHash,
            Keccak endHash,
            Keccak limitHash,
            Keccak expectedRootHash,
            byte[][]? proofs = null
        )
        {
            if (proofs is null || proofs.Length == 0)
            {
                return (AddRangeResult.OK, null, false);
            }

            if (tree is null)
            {
                throw new ArgumentNullException(nameof(tree));
            }

            startingHash ??= Keccak.Zero;
            List<TrieNode> sortedBoundaryList = new();

            Dictionary<Keccak, TrieNode> dict = CreateProofDict(proofs, tree.TrieStore);

            if (!dict.TryGetValue(expectedRootHash, out TrieNode root))
            {
                return (AddRangeResult.MissingRootHashInProofs, null, true);
            }

            Span<byte> leftBoundary = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(startingHash.Bytes, leftBoundary);
            Span<byte> rightBoundary = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(endHash.Bytes, rightBoundary);
            Span<byte> rightLimit = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(limitHash.Bytes, rightLimit);

            // For when in very-very unlikely case where the last remaining address is Keccak.MaxValue, (who knows why,
            // the chain have special handling for it maybe) and it is not included the returned account range, (again,
            // very-very unlikely), we want `moreChildrenToRight` to return true.
            bool noLimit = limitHash == Keccak.MaxValue;

            Stack<(TrieNode parent, TrieNode node, int pathIndex, List<byte> path)> proofNodesToProcess = new();

            root.StoreNibblePathPrefix = tree.StoreNibblePathPrefix;
            tree.RootRef = root;
            proofNodesToProcess.Push((null, root, -1, new List<byte>()));
            sortedBoundaryList.Add(root);

            bool moreChildrenToRight = false;

            while (proofNodesToProcess.Count > 0)
            {
                (TrieNode parent, TrieNode node, int pathIndex, List<byte> path) = proofNodesToProcess.Pop();

                node.PathToNode = path.ToArray();
                node.StoreNibblePathPrefix = tree.StoreNibblePathPrefix;
                //Console.WriteLine($"Node {node.PathToNode.ToHexString()} hash: {node.Keccak}");
                if (node.IsExtension)
                {
                    Keccak? childKeccak = node.GetChildHash(0);

                    if (childKeccak is not null)
                    {
                        if (dict.TryGetValue(childKeccak, out TrieNode child))
                        {
                            node.SetChild(0, child);

                            pathIndex += node.Key.Length;
                            path.AddRange(node.Key);
                            proofNodesToProcess.Push((node, child, pathIndex, path));
                            sortedBoundaryList.Add(child);
                        }
                        else
                        {
                            Span<byte> pathSpan = CollectionsMarshal.AsSpan(path);
                            if (Bytes.Comparer.Compare(pathSpan, leftBoundary[0..path.Count]) >= 0
                                && parent is not null
                                && parent.IsBranch)
                            {
                                for (int i = 0; i < 15; i++)
                                {
                                    Keccak? kec = parent.GetChildHash(i);
                                    if (kec == node.Keccak)
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
                    int left = Bytes.Comparer.Compare(pathSpan, leftBoundary[0..path.Count]) == 0 ? leftBoundary[pathIndex] : 0;
                    int right = Bytes.Comparer.Compare(pathSpan, rightBoundary[0..path.Count]) == 0 ? rightBoundary[pathIndex] : 15;
                    int limit = Bytes.Comparer.Compare(pathSpan, rightLimit[0..path.Count]) == 0 ? rightLimit[pathIndex] : 15;

                    int maxIndex = moreChildrenToRight ? right : 15;

                    for (int ci = left; ci <= maxIndex; ci++)
                    {
                        Keccak? childKeccak = node.GetChildHash(ci);

                        moreChildrenToRight |= (ci > right && (ci < limit || noLimit)) && childKeccak is not null;

                        if (ci >= left && ci <= right)
                        {
                            node.SetChild(ci, null);
                        }

                        if (childKeccak is not null && (ci == left || ci == right) && dict.TryGetValue(childKeccak, out TrieNode child))
                        {
                            if (!child.IsLeaf)
                            {
                                node.SetChild(ci, child);

                                // TODO: we should optimize it - copy only if there are two boundary children
                                List<byte> newPath = new(path);

                                newPath.Add((byte)ci);

                                proofNodesToProcess.Push((node, child, pathIndex, newPath));
                                sortedBoundaryList.Add(child);
                            }
                        }
                    }
                }
            }

            return (AddRangeResult.OK, sortedBoundaryList, moreChildrenToRight);
        }

        private static Dictionary<Keccak, TrieNode> CreateProofDict(byte[][] proofs, ITrieStore store)
        {
            Dictionary<Keccak, TrieNode> dict = new();

            for (int i = 0; i < proofs.Length; i++)
            {
                byte[] proof = proofs[i];
                var node = new TrieNode(NodeType.Unknown, proof, true);
                node.IsBoundaryProofNode = true;
                node.ResolveNode(store);
                node.ResolveKey(store, i == 0);

                dict[node.Keccak] = node;
            }

            return dict;
        }

        private static void StitchBoundaries(IList<TrieNode> sortedBoundaryList, ITrieStore store)
        {
            if (sortedBoundaryList is null || sortedBoundaryList.Count == 0)
            {
                return;
            }

            for (int i = sortedBoundaryList.Count - 1; i >= 0; i--)
            {
                TrieNode node = sortedBoundaryList[i];

                if (!node.IsPersisted)
                {
                    if (node.IsExtension)
                    {
                        if (IsChildPersisted(node, 1, store))
                        {
                            node.IsBoundaryProofNode = false;
                        }
                    }

                    if (node.IsBranch)
                    {
                        bool isBoundaryProofNode = false;
                        for (int ci = 0; ci <= 15; ci++)
                        {
                            if (!IsChildPersisted(node, ci, store))
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

        private static bool IsChildPersisted(TrieNode node, int childIndex, ITrieStore store)
        {
            TrieNode data = node.GetData(childIndex) as TrieNode;
            if (data is not null)
            {

                return data.IsBoundaryProofNode == false;
            }

            Keccak childKeccak = node.GetChildHash(childIndex);
            return childKeccak is null || store.IsPersisted(childKeccak);
        }
    }
}
