// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
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
        public static (AddRangeResult result, bool moreChildrenToRight, List<PathWithAccount> storageRoots, List<ValueKeccak> codeHashes) AddAccountRange(
            IStateTree tree,
            long blockNumber,
            in ValueKeccak expectedRootHash,
            in ValueKeccak startingHash,
            in ValueKeccak limitHash,
            PathWithAccount[] accounts,
            byte[][] proofs = null
        )
        {
            // TODO: Check the accounts boundaries and sorting

            ValueKeccak lastHash = accounts[^1].Path;

            (AddRangeResult result, List<TrieNode> sortedBoundaryList, bool moreChildrenToRight) =
                FillBoundaryTree(tree, startingHash, lastHash, limitHash, expectedRootHash, proofs);

            if (result != AddRangeResult.OK)
            {
                return (result, true, null, null);
            }

            List<PathWithAccount> accountsWithStorage = new();
            List<ValueKeccak> codeHashes = new();

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

            tree.Commit(blockNumber, skipRoot: true, WriteFlags.DisableWAL);

            return (AddRangeResult.OK, moreChildrenToRight, accountsWithStorage, codeHashes);
        }

        public static (AddRangeResult result, bool moreChildrenToRight) AddStorageRange(
            StorageTree tree,
            long blockNumber,
            in ValueKeccak? startingHash,
            PathWithStorageSlot[] slots,
            in ValueKeccak expectedRootHash,
            byte[][]? proofs = null
        )
        {
            // TODO: Check the slots boundaries and sorting

            ValueKeccak lastHash = slots[^1].Path;

            (AddRangeResult result, List<TrieNode> sortedBoundaryList, bool moreChildrenToRight) = FillBoundaryTree(
                tree, startingHash, lastHash, ValueKeccak.MaxValue, expectedRootHash, proofs);

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

            tree.Commit(blockNumber, writeFlags: WriteFlags.DisableWAL);

            return (AddRangeResult.OK, moreChildrenToRight);
        }

        [SkipLocalsInit]
        private static (AddRangeResult result, List<TrieNode> sortedBoundaryList, bool moreChildrenToRight) FillBoundaryTree(
            IPatriciaTree tree,
            in ValueKeccak? startingHash,
            in ValueKeccak endHash,
            in ValueKeccak limitHash,
            in ValueKeccak expectedRootHash,
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

            ValueKeccak effectiveStartingHAsh = startingHash.HasValue ? startingHash.Value : ValueKeccak.Zero;
            List<TrieNode> sortedBoundaryList = new();

            Dictionary<ValueKeccak, TrieNode> dict = CreateProofDict(proofs, tree.TrieStore);

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
                    if (node.GetChildHashAsValueKeccak(0, out ValueKeccak childKeccak))
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
                                    if (parent.GetChildHashAsValueKeccak(i, out ValueKeccak kec) && kec == node.Keccak)
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
                        bool hasKeccak = node.GetChildHashAsValueKeccak(ci, out ValueKeccak childKeccak);

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
                                sortedBoundaryList.Add(child);
                            }
                        }
                    }
                }
            }

            return (AddRangeResult.OK, sortedBoundaryList, moreChildrenToRight);
        }

        private static Dictionary<ValueKeccak, TrieNode> CreateProofDict(byte[][] proofs, ITrieStore store)
        {
            Dictionary<ValueKeccak, TrieNode> dict = new();

            for (int i = 0; i < proofs.Length; i++)
            {
                byte[] proof = proofs[i];
                if (store.Capability == TrieNodeResolverCapability.Path)
                {
                    //a workaround to correctly resolve a node for path based store - avoids recalc of keccak for child node
                    TrieNode node = new(NodeType.Unknown, proof, false)
                    {
                        IsBoundaryProofNode = true
                    };
                    node.ResolveNode(store);
                    node.ResolveKey(store, i == 0);

                    dict[node.Keccak] = node.Clone();
                    dict[node.Keccak].IsBoundaryProofNode = true;
                }
                else
                {
                    TrieNode node = new(NodeType.Unknown, proof, true)
                    {
                        IsBoundaryProofNode = true
                    };
                    node.ResolveNode(store);
                    node.ResolveKey(store, i == 0);
                    dict[node.Keccak] = node;
                }
            }

            return dict;
        }

        private static void StitchBoundaries(List<TrieNode> sortedBoundaryList, ITrieStore store)
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

            if (store.Capability == TrieNodeResolverCapability.Hash)
            {
                if (!node.GetChildHashAsValueKeccak(childIndex, out ValueKeccak childKeccak))
                    return true;
                return store.IsPersisted(childKeccak);
            }
            else
            {
                Keccak childKeccak = node.GetChildHash(childIndex);
                if (childKeccak is null)
                    return true;

                if (node.IsBranch)
                {
                    Span<byte> childPath = stackalloc byte[node.FullPath.Length + 1];
                    node.FullPath.CopyTo(childPath);
                    childPath[^1] = (byte)childIndex;
                    return store.ExistsInDB(childKeccak, childPath.ToArray());
                }
                else if (node.IsExtension)
                {
                    Span<byte> childPath = stackalloc byte[node.FullPath.Length + node.Key.Length];
                    node.FullPath.CopyTo(childPath);
                    node.Key.CopyTo(childPath.Slice(node.FullPath.Length));
                    return store.ExistsInDB(childKeccak, childPath.ToArray());
                }
                return false;
            }
        }
    }
}
