// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Stats.Model;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using static System.Reflection.Metadata.BlobBuilder;
using static Nethermind.Core.Extensions.Bytes;
using Rlp = Nethermind.Serialization.Rlp.Rlp;

namespace Nethermind.Synchronization.SnapSync
{
    public static class SnapProviderHelperPaprika
    {
        public static (AddRangeResult result, bool moreChildrenToRight, List<PathWithAccount> storageRoots, List<ValueHash256> codeHashes) AddAccountRange(
            IRawState state,
            long blockNumber,
            in ValueHash256 expectedRootHash,
            in ValueHash256 startingHash,
            in ValueHash256 limitHash,
            IReadOnlyList<PathWithAccount> accounts,
            IReadOnlyList<byte[]> proofs = null
        )
        {
            // TODO: Check the accounts boundaries and sorting

            ValueHash256 lastHash = accounts[^1].Path;
            
            StateTree tree = new StateTree();

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

                //TODO - should add add snap sync stats?
                state.SetAccount(account.Path, account.Account);
            }


            TreePath firstPath = TreePath.FromPath(startingHash.BytesAsSpan);
            TreePath lastPath = TreePath.FromPath(lastHash.BytesAsSpan);

            TreePath treePath = TreePath.Empty;

            if (sortedBoundaryList?.Count > 0)
            {
                FillInProofNodes(state, Keccak.Zero, firstPath, lastPath, sortedBoundaryList[0].Item1, treePath);
            }

            ValueHash256 newStateRoot = state.RefreshRootHash();
            if (newStateRoot != expectedRootHash)
            {
                //using var fileStream = new FileStream(@$"C:\Temp\case_{startingHash}_{lastHash}.txt", FileMode.Create);
                //using var sw = new StreamWriter(fileStream);
                ////sw.WriteLine($"{account.Path.ToString()}|{account.Account.Balance}|{account.Account.StorageRoot}");
                //sw.WriteLine(startingHash.ToString());
                //sw.WriteLine(expectedRootHash.ToString());
                //sw.WriteLine(newStateRoot.ToString());

                //for (var index = 0; index < accounts.Count; index++)
                //{
                //    PathWithAccount pwa = accounts[index];
                //    sw.WriteLine($"{pwa.Path.ToString()}|{pwa.Account.Nonce}|{pwa.Account.Balance}|{pwa.Account.StorageRoot}|{pwa.Account.CodeHash}");
                //}

                //sw.WriteLine();
                //for (var index = 0; index < proofs?.Count; index++)
                //{
                //    sw.WriteLine($"{proofs[index].ToHexString()}");
                //}

                state.Discard();
                return (AddRangeResult.DifferentRootHash, true, null, null);
            }

            state.Commit(false);
            return (AddRangeResult.OK, moreChildrenToRight, accountsWithStorage, codeHashes);
        }

        private static bool IsNotInRange(TreePath currentPath, TreePath firstPath, TreePath lastPath)
        {
            return currentPath.CompareToTruncatedWithEquality(firstPath, currentPath.Length) < 0 ||
                   currentPath.CompareToTruncatedWithEquality(lastPath, currentPath.Length) > 0;
        }

        private static void FillInProofNodes(IRawState state, ValueHash256 accountHash, TreePath firstPath, TreePath lastPath, TrieNode node, TreePath treePath)
        {
            TreePath emptyPath = TreePath.Empty;
            if (node.IsExtension)
            {
                TreePath childPath = treePath.Append(node.Key);
                FillInProofNodes(state, accountHash, firstPath, lastPath, node.GetChild(NullTrieNodeResolver.Instance, ref emptyPath, 0), childPath);
                state.CreateProofExtension(accountHash, childPath.Span, treePath.Length, node.Key.Length, false);
            }
            else if (node.IsBranch)
            {
                List<byte> children = new List<byte>();
                List<Hash256?> childHashes = new List<Hash256?>();
                for (byte i = 0; i < 16; i++)
                {
                    TreePath childPath = treePath.Append(i);
                    if (IsNotInRange(childPath, firstPath, lastPath))
                    {
                        if (node.GetChildHashAsValueKeccak(i, out ValueHash256 childHash))
                        {
                            children.Add((byte)i);
                            childHashes.Add(childHash.ToCommitment());
                        }
                        else
                        {
                            TrieNode childNode = node.GetChild(NullTrieNodeResolver.Instance, ref emptyPath, i);
                            if (childNode is not null)
                            {
                                children.Add((byte)i);
                                childHashes.Add(null);
                                childNode.ResolveNode(NullTrieNodeResolver.Instance, in emptyPath);

                                TreePath inlineChild = childPath.Append(childNode.Key);
                                state.SetStorage(accountHash, inlineChild.Path, childNode.Value);
                            }
                        }
                    }
                    else
                    {
                        TrieNode childNode = node.GetChild(NullTrieNodeResolver.Instance, ref emptyPath, i);
                        if (childNode is not null)
                        {
                            FillInProofNodes(state, accountHash, firstPath, lastPath, childNode, childPath);
                            children.Add((byte)i);
                            childHashes.Add(null);
                        }
                        else
                        {
                            if (!node.GetChildHashAsValueKeccak(i, out ValueHash256 childHash)) continue;
                            children.Add((byte)i);
                            childHashes.Add(null);
                        }
                    }
                }

                if (children.Count < 2)
                {
                    Console.WriteLine($"Less than 2 children for branch node {treePath.ToHexString()} | {treePath.Length} | {node.FullRlp.ToArray()?.ToHexString()}");
                }
                else
                {
                    state.CreateProofBranch(accountHash, treePath.Span, treePath.Length, children.ToArray(),
                        childHashes.ToArray(), false);
                }
            }
        }

        public static (AddRangeResult result, bool moreChildrenToRight) AddStorageRange(
            IRawState state,
            PathWithAccount account,
            long blockNumber,
            in ValueHash256? startingHash,
            IReadOnlyList<PathWithStorageSlot> slots,
            in ValueHash256 expectedRootHash,
            IReadOnlyList<byte[]>? proofs = null
        )
        {
            // TODO: Check the slots boundaries and sorting

            StorageTree tree = new StorageTree(new ScopedTrieStore(new TrieStore(new MemDb(), NullLogManager.Instance), new Hash256(account.Path)),
                NullLogManager.Instance);

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

                Rlp.ValueDecoderContext rlpContext = new Rlp.ValueDecoderContext(slot.SlotRlpValue);
                state.SetStorage(account.Path, slot.Path, rlpContext.DecodeByteArray());
            }

            TreePath firstPath = TreePath.FromPath((startingHash ?? slots[0].Path).BytesAsSpan);
            TreePath lastPath = TreePath.FromPath(lastHash.BytesAsSpan);

            if (sortedBoundaryList?.Count > 0)
            {
                TreePath treePath = TreePath.Empty;
                FillInProofNodes(state, account.Path, firstPath, lastPath, sortedBoundaryList[0].Item1, treePath);
            }


            ValueHash256 newStorageRoot = state.RecalculateStorageRoot(account.Path);

            if (newStorageRoot != expectedRootHash)
            {
                //using var fileStream = new FileStream(@$"C:\Temp\case_{account.Path}.txt", FileMode.Create);
                //using var sw = new StreamWriter(fileStream);
                //sw.WriteLine($"{account.Path.ToString()}|{account.Account.Balance}|{account.Account.StorageRoot}");
                //sw.WriteLine(startingHash.ToString());
                //sw.WriteLine(expectedRootHash.ToString());
                //sw.WriteLine(newStorageRoot.ToString());

                //for (var index = 0; index < slots.Count; index++)
                //{
                //    PathWithStorageSlot slot = slots[index];
                //    sw.WriteLine($"{slot.Path.ToString()}|{slot.SlotRlpValue.ToHexString()}");
                //}

                //sw.WriteLine();
                //for (var index = 0; index < proofs?.Count; index++)
                //{
                //    sw.WriteLine($"{proofs[index].ToHexString()}");
                //}

                state.Discard();
                return (AddRangeResult.DifferentRootHash, true);
            }

            state.Commit(false);

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
                            //node.SetChild(ci, null);
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
                if (proof.Length == 0)
                    continue;
                TrieNode node = new(NodeType.Unknown, proof, isDirty: true);
                node.IsBoundaryProofNode = true;

                TreePath emptyPath = TreePath.Empty;
                node.ResolveNode(store, emptyPath);
                node.ResolveKey(store, ref emptyPath, isRoot: i == 0);

                dict[node.Keccak] = node;
            }

            return dict;
        }
    }
}
