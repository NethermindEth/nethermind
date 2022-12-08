//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//
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
    internal static class SnapProviderHelper
    {
        private static object _syncCommit = new();

        public static AddAccountRangeResult AddAccountRange(StateTree tree, long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null)
        {
            // TODO: Check the accounts boundaries and sorting

            Keccak lastHash = accounts[^1].Path;
            long syncedAcountBytes = 0;

            (AddRangeResult result, IList<TrieNode> sortedBoundaryList, bool moreChildrenToRight) = FillBoundaryTree(tree, startingHash, lastHash, expectedRootHash, proofs);

            if (result != AddRangeResult.OK)
            {
                return new AddAccountRangeResult(result, true, null, null);
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
                    syncedAcountBytes += rlp.Bytes.Length;
                }
            }

            tree.UpdateRootHash();

            if (tree.RootHash != expectedRootHash)
            {
                return new AddAccountRangeResult(AddRangeResult.DifferentRootHash, true, null, null);
            }

            syncedAcountBytes += StitchBoundaries(sortedBoundaryList, tree.TrieStore);

            lock (_syncCommit)
            {
                tree.Commit(blockNumber);
            }

            return new AddAccountRangeResult(AddRangeResult.OK, moreChildrenToRight, accountsWithStorage, codeHashes, syncedAcountBytes);
        }

        public static AddStorageRangeResult
            AddStorageRange(StorageTree tree, long blockNumber, Keccak? startingHash, PathWithStorageSlot[] slots, Keccak expectedRootHash, byte[][]? proofs = null)
        {
            // TODO: Check the slots boundaries and sorting

            Keccak lastHash = slots.Last().Path;
            long syncedBytes = 0;

            (AddRangeResult result, IList<TrieNode> sortedBoundaryList, bool moreChildrenToRight) = FillBoundaryTree(tree, startingHash, lastHash, expectedRootHash, proofs);

            if (result != AddRangeResult.OK)
            {
                return new(result, true);
            }

            for (var index = 0; index < slots.Length; index++)
            {
                PathWithStorageSlot slot = slots[index];
                Interlocked.Add(ref Metrics.SnapStateSynced, slot.SlotRlpValue.Length);
                syncedBytes += slot.SlotRlpValue.Length;
                tree.Set(slot.Path, slot.SlotRlpValue, false);
            }

            tree.UpdateRootHash();

            if (tree.RootHash != expectedRootHash)
            {
                return new(AddRangeResult.DifferentRootHash, true);
            }

            syncedBytes = StitchBoundaries(sortedBoundaryList, tree.TrieStore);

            lock (_syncCommit)
            {
                tree.Commit(blockNumber);
            }

            return new(AddRangeResult.OK, moreChildrenToRight, syncedBytes);
        }

        private static (AddRangeResult result, IList<TrieNode> sortedBoundaryList, bool moreChildrenToRight) FillBoundaryTree(PatriciaTree tree, Keccak? startingHash, Keccak endHash, Keccak expectedRootHash, byte[][]? proofs = null)
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

            Stack<(TrieNode parent, TrieNode node, int pathIndex, List<byte> path)> proofNodesToProcess = new();

            tree.RootRef = root;
            proofNodesToProcess.Push((null, root, -1, new List<byte>()));
            sortedBoundaryList.Add(root); ;

            bool moreChildrenToRight = false;

            while (proofNodesToProcess.Count > 0)
            {
                (TrieNode parent, TrieNode node, int pathIndex, List<byte> path) = proofNodesToProcess.Pop();

                if (node.IsExtension)
                {
                    Keccak? childKeccak = node.GetChildHash(0);

                    if (childKeccak is not null)
                    {
                        if (dict.TryGetValue(childKeccak, out TrieNode child))
                        {
                            node.SetChild(0, child);

                            pathIndex += node.Path.Length;
                            path.AddRange(node.Path);
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

                    int maxIndex = moreChildrenToRight ? right : 15;

                    for (int ci = left; ci <= maxIndex; ci++)
                    {
                        Keccak? childKeccak = node.GetChildHash(ci);

                        moreChildrenToRight |= ci > right && childKeccak is not null;

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

        private static int StitchBoundaries(IList<TrieNode> sortedBoundaryList, ITrieStore store)
        {
            if (sortedBoundaryList is null || sortedBoundaryList.Count == 0)
            {
                return 0;
            }

            int stichesBytesSize = 0;
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

                    if (!node.IsBoundaryProofNode)
                        stichesBytesSize += node.FullRlp?.Length ?? 0;
                }
            }
            return stichesBytesSize;
        }

        private static bool IsChildPersisted(TrieNode node, int childIndex, ITrieStore store)
        {
            TrieNode data = node.GetData(childIndex) as TrieNode;
            if (data is not null)
            {

                return data.IsBoundaryProofNode == false;
            }

            Keccak childKeccak = node.GetChildHash(childIndex);
            if (childKeccak is null)
            {
                return true;
            }

            return store.IsPersisted(childKeccak);
        }
    }
}
