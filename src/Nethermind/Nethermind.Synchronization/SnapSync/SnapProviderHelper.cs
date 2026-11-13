// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync
{
    public static class SnapProviderHelper
    {
        private const int ExtensionRlpChildIndex = 1;

        public static (AddRangeResult result, bool moreChildrenToRight, List<PathWithAccount> storageRoots, List<ValueHash256> codeHashes, Hash256 actualRootHash) AddAccountRange(
            ISnapTrieFactory factory,
            ulong blockNumber,
            in ValueHash256 expectedRootHash,
            in ValueHash256 startingHash,
            in ValueHash256 limitHash,
            IReadOnlyList<PathWithAccount> accounts,
            IByteArrayList proofs = null
        )
        {
            using ISnapTree<PathWithAccount> tree = factory.CreateStateTree();

            (AddRangeResult result, bool moreChildrenToRight, _) = CommitRange(
                tree, accounts, startingHash, limitHash, expectedRootHash, proofs);
            if (result != AddRangeResult.OK)
                return (result, true, null, null, tree.RootHash);

            List<PathWithAccount> accountsWithStorage = [];
            List<ValueHash256> codeHashes = [];
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
            IByteArrayList? proofs = null
        )
        {
            using ISnapTree<PathWithStorageSlot> tree = factory.CreateStorageTree(account.Path);

            ValueHash256 effectiveLimitHash = limitHash ?? Keccak.MaxValue;
            ValueHash256 effectiveStartingHash = startingHash ?? ValueKeccak.Zero;

            (AddRangeResult result, bool moreChildrenToRight, bool isRootPersisted) = CommitRange(
                tree, slots, effectiveStartingHash, effectiveLimitHash, account.Account.StorageRoot, proofs);
            if (result != AddRangeResult.OK)
                return (result, true, tree.RootHash, false);
            return (AddRangeResult.OK, moreChildrenToRight, tree.RootHash, isRootPersisted);
        }

        /// <summary>
        /// Verifies that <paramref name="accounts"/> and <paramref name="proofs"/> reconstruct
        /// <paramref name="expectedRootHash"/> for the range starting at <paramref name="startingHash"/>, without
        /// committing anything. Intended for single-account refresh against an isolated, empty-backed factory:
        /// the boundary nodes and the returned accounts are assembled in memory only to recompute the root, so an
        /// incomplete proof fails verification rather than being completed from (or racing) the live state DB.
        /// </summary>
        public static AddRangeResult VerifyAccountRange(
            ISnapTrieFactory factory,
            in ValueHash256 expectedRootHash,
            in ValueHash256 startingHash,
            in ValueHash256 limitHash,
            IReadOnlyList<PathWithAccount> accounts,
            IByteArrayList? proofs)
        {
            using ISnapTree<PathWithAccount> tree = factory.CreateStateTree();
            return BuildAndVerifyRoot(tree, accounts, startingHash, limitHash, expectedRootHash, proofs).result;
        }

        private static (AddRangeResult result, bool moreChildrenToRight, bool isRootPersisted) CommitRange<TEntry>(
            ISnapTree<TEntry> tree,
            IReadOnlyList<TEntry> entries,
            in ValueHash256 startingHash,
            in ValueHash256 limitHash,
            in ValueHash256 expectedRootHash,
            IByteArrayList? proofs) where TEntry : ISnapEntry
        {
            (AddRangeResult result, bool moreChildrenToRight) =
                BuildAndVerifyRoot(tree, entries, startingHash, limitHash, expectedRootHash, proofs);

            if (result != AddRangeResult.OK)
                return (result, true, false);

            // The upper bound is used to prevent proof nodes that covers next range from being persisted, except if
            // this is the last range. This prevent double node writes per path which break flat. It also prevent leaf o
            // that is after the range from being persisted, which prevent double write again.
            ValueHash256 lastPath = entries.Count == 0 ? limitHash : entries[entries.Count - 1].Path;
            ValueHash256 upperBound = lastPath;
            if (upperBound > limitHash)
            {
                upperBound = limitHash;
            }
            else
            {
                if (!moreChildrenToRight) upperBound = ValueKeccak.MaxValue;
            }

            bool isRootPersisted = tree.Commit(upperBound);
            return (AddRangeResult.OK, moreChildrenToRight, isRootPersisted);
        }

        /// <summary>
        /// Fills the boundary proof nodes and bulk-sets the range entries into <paramref name="tree"/> in memory,
        /// then checks that the recomputed root equals <paramref name="expectedRootHash"/>. Nothing is committed.
        /// </summary>
        private static (AddRangeResult result, bool moreChildrenToRight) BuildAndVerifyRoot<TEntry>(
            ISnapTree<TEntry> tree,
            IReadOnlyList<TEntry> entries,
            in ValueHash256 startingHash,
            in ValueHash256 limitHash,
            in ValueHash256 expectedRootHash,
            IByteArrayList? proofs) where TEntry : ISnapEntry
        {
            // Without entries a proof is a proof of absence: on its own it has to rebuild the root with all of
            // [startingHash, limitHash] cleared, which leaves nothing more to fetch in the range.
            bool isProofOfAbsence = entries.Count == 0;
            if (isProofOfAbsence && proofs is not { Count: > 0 })
                return (AddRangeResult.EmptyRange, false);

            // Validate sorting order
            for (int i = 1; i < entries.Count; i++)
            {
                if (entries[i - 1].Path.CompareTo(entries[i].Path) >= 0)
                    return (AddRangeResult.InvalidOrder, false);
            }

            if (!isProofOfAbsence && entries[0].Path < startingHash)
                return (AddRangeResult.InvalidOrder, false);

            ValueHash256 lastPath = isProofOfAbsence ? limitHash : entries[entries.Count - 1].Path;

            AddRangeResult result;
            bool moreChildrenToRight;
            try
            {
                (result, moreChildrenToRight) =
                    FillBoundaryTree(tree, startingHash, lastPath, limitHash, expectedRootHash, proofs);
            }
            catch (Exception)
            {
                return (AddRangeResult.InvalidProof, false);
            }

            if (result != AddRangeResult.OK)
                return (result, false);

            try
            {
                tree.BulkSetAndUpdateRootHash(entries);
            }
            catch (TrieException e) when (e is MissingTrieNodeException or TrieNodeException)
            {
                // The tree holds only the proof nodes, so the bulk set loads a node only when an entry reaches a child
                // the proof keeps as a hash, such as a leaf beside the range: the entries do not fit the proof. Patricia
                // stores report the node they cannot load as a MissingTrieNodeException, flat ones as a TrieNodeException.
                return (AddRangeResult.InvalidProof, false);
            }

            if (tree.RootHash.ValueHash256 != expectedRootHash)
                return (AddRangeResult.DifferentRootHash, false);

            return (AddRangeResult.OK, moreChildrenToRight && !isProofOfAbsence);
        }

        [SkipLocalsInit]
        private static (AddRangeResult result, bool moreChildrenToRight) FillBoundaryTree<TEntry>(
            ISnapTree<TEntry> tree,
            in ValueHash256? startingHash,
            in ValueHash256 endHash,
            in ValueHash256 limitHash,
            in ValueHash256 expectedRootHash,
            IByteArrayList? proofs = null
        ) where TEntry : ISnapEntry
        {
            if (proofs is null || proofs.Count == 0)
            {
                return (AddRangeResult.OK, false);
            }

            ArgumentNullException.ThrowIfNull(tree);

            ValueHash256 effectiveStartingHash = startingHash ?? ValueKeccak.Zero;
            List<SnapProofNode> sortedBoundaryList = [];

            Dictionary<ValueHash256, int> dict = CreateProofDict(proofs);

            if (!dict.TryGetValue(expectedRootHash, out int rootIndex))
            {
                return (AddRangeResult.MissingRootHashInProofs, true);
            }

            TrieNodeRlpRef root = new(proofs[rootIndex]);
            if (dict.Count == 1 && root.IsLeaf)
            {
                // Special case with some server sending proof where the root is the same as the only path.
                // Without this the proof's IsBoundaryNode flag will cause the key to not get saved.
                TreePath rootPath = TreePath.FromNibble(root.Key);
                if (rootPath.Length == 64 && rootPath.Path.Equals(endHash))
                {
                    return (AddRangeResult.OK, false);
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
            Stack<int> proofNodesToProcess = new();

            proofNodesToProcess.Push(sortedBoundaryList.Count);
            sortedBoundaryList.Add(new SnapProofNode(rootIndex, TreePath.Empty, -1, 0, 0));

            bool moreChildrenToRight = false;
            while (proofNodesToProcess.Count > 0)
            {
                int nodeIndex = proofNodesToProcess.Pop();
                TreePath path = sortedBoundaryList[nodeIndex].Path;
                TrieNodeRlpRef node = new(proofs[sortedBoundaryList[nodeIndex].ProofIndex]);

                if (node.IsExtension)
                {
                    if (node.Key is not { Length: > 0 })
                    {
                        return (AddRangeResult.InvalidProofNode, true);
                    }

                    if (node.GetChildHashAsValueKeccak(ExtensionRlpChildIndex, out ValueHash256 childKeccak))
                    {
                        TreePath childPath = path.Append(node.Key);

                        moreChildrenToRight |= childPath.Path > limitHash;

                        if (childPath.CompareTo(leftBoundaryPath.Truncate(childPath.Length)) >= 0)
                        {
                            if (dict.TryGetValue(childKeccak, out int child))
                            {
                                proofNodesToProcess.Push(sortedBoundaryList.Count);
                                sortedBoundaryList.Add(new SnapProofNode(child, childPath, nodeIndex, 0, 0));
                            }
                            else if (childPath.CompareTo(rightBoundaryPath.Truncate(childPath.Length)) <= 0)
                            {
                                // The child's keys overlap [start, end]: the path of the start or of the end runs through
                                // this extension, or it is a root extension inside the range. Kept as a hash, the child
                                // would stand for every key of the range under it: the entries could leave them out and
                                // the root would still match.
                                return (AddRangeResult.InvalidProof, true);
                            }
                        }
                    }
                }
                else if (node.IsBranch)
                {
                    int left = leftBoundaryPath.CompareToTruncated(path, path.Length) == 0 ? leftBoundaryPath[path.Length] : 0;
                    int right = rightBoundaryPath.CompareToTruncated(path, path.Length) == 0 ? rightBoundaryPath[path.Length] : 15;

                    int maxIndex = moreChildrenToRight ? right : 15;

                    ushort clearedChildren = 0;
                    for (int ci = left; ci <= maxIndex; ci++)
                    {
                        bool hasKeccak = node.GetChildHashAsValueKeccak(ci, out ValueHash256 childKeccak);
                        int childProofIndex = -1;
                        if (hasKeccak && dict.TryGetValue(childKeccak, out int proofIndex))
                        {
                            childProofIndex = proofIndex;
                        }

                        TrieNodeRlpRef child = default;
                        bool hasChild = childProofIndex >= 0;
                        if (hasChild)
                        {
                            child = new TrieNodeRlpRef(proofs[childProofIndex]);
                        }
                        else
                        {
                            // Note: be careful with inline node. Inline node is not set in the proof dictionary
                            ReadOnlySpan<byte> inlineRlp = node.GetInlineNodeRlp(ci);
                            if (!inlineRlp.IsEmpty)
                            {
                                child = new TrieNodeRlpRef(inlineRlp);
                                hasChild = true;
                            }
                        }

                        // The limit may have lower nibble that is less than the path's current nibble, even if upper
                        // nibble is higher. So need to check whole path
                        TreePath childPath = path.Append(ci);
                        moreChildrenToRight |= (hasKeccak || hasChild) && (ci > right && (childPath.Path <= limitHash || noLimit));

                        if (ci >= left && ci <= right)
                        {
                            // Clear child within boundary
                            clearedChildren |= (ushort)(1 << ci);
                        }

                        if (hasChild && !hasKeccak && (ci == left || ci == right))
                        {
                            // Inline node at boundary. Need to be set back or keccak will be incorrect.
                            // but must not be set as part of boundary list or break stitching.
                            TreePath wholePath = childPath.Append(child.Key);
                            if (leftBoundaryPath.CompareToTruncated(wholePath, wholePath.Length) > 0 || rightBoundaryPath.CompareToTruncated(wholePath, wholePath.Length) < 0)
                            {
                                clearedChildren &= (ushort)~(1 << ci);
                            }
                        }

                        if (hasKeccak && (ci == left || ci == right) && hasChild)
                        {
                            if (child.IsBranch)
                            {
                                clearedChildren &= (ushort)~(1 << ci);

                                proofNodesToProcess.Push(sortedBoundaryList.Count);
                                sortedBoundaryList.Add(new SnapProofNode(childProofIndex, childPath, nodeIndex, ci, 0));
                            }
                            else if (child.IsExtension)
                            {
                                // If its an extension, its path + key must be outside or equal to the boundary.
                                TreePath wholePath = childPath.Append(child.Key);
                                if (leftBoundaryPath.CompareToTruncated(wholePath, wholePath.Length) >= 0 || rightBoundaryPath.CompareToTruncated(wholePath, wholePath.Length) <= 0)
                                {
                                    clearedChildren &= (ushort)~(1 << ci);
                                    proofNodesToProcess.Push(sortedBoundaryList.Count);
                                    sortedBoundaryList.Add(new SnapProofNode(childProofIndex, childPath, nodeIndex, ci, 0));
                                }
                            }
                            else
                            {
                                // If its a leaf, its path + key must be outside the boundary.
                                TreePath wholePath = childPath.Append(child.Key);
                                if (leftBoundaryPath.CompareToTruncated(wholePath, wholePath.Length) > 0 || rightBoundaryPath.CompareToTruncated(wholePath, wholePath.Length) < 0)
                                {
                                    // Leaf are range proof, proof that the range covers the requested path.
                                    // But we dont persist them as it should be persisted by the range itself through
                                    // other range.
                                    clearedChildren &= (ushort)~(1 << ci);
                                }
                            }
                        }
                    }

                    sortedBoundaryList[nodeIndex] = sortedBoundaryList[nodeIndex] with { ClearedChildren = clearedChildren };
                }
            }

            tree.SetRootFromProof(proofs, sortedBoundaryList);
            return (AddRangeResult.OK, moreChildrenToRight);
        }

        private static Dictionary<ValueHash256, int> CreateProofDict(IByteArrayList proofs)
        {
            Dictionary<ValueHash256, int> dict = [];

            for (int i = 0; i < proofs.Count; i++)
            {
                ReadOnlySpan<byte> proof = proofs[i];

                // Rejects a malformed proof even when no path reaches it.
                _ = new TrieNodeRlpRef(proof);

                dict[ValueKeccak.Compute(proof)] = i;
            }

            return dict;
        }

        /// <summary>A proof node read in place from its RLP, as far as the boundary walk needs it.</summary>
        private readonly ref struct TrieNodeRlpRef
        {
            private const int LeafOrExtensionItemCount = 2;

            // Sixteen children and a value.
            private const int BranchItemCount = 17;

            private readonly ReadOnlySpan<byte> _rlp;

            /// <exception cref="RlpReaderException">The RLP is malformed.</exception>
            /// <exception cref="TrieException">The RLP is not one list of the items of a branch, an extension or a leaf.</exception>
            public TrieNodeRlpRef(ReadOnlySpan<byte> rlp)
            {
                _rlp = rlp;
                NodeRlpReader reader = new(rlp);
                int position = 0;
                reader.ReadSequenceLength(ref position, out int length);
                int end = position + length;
                int itemCount = 0;
                for (; position < end && itemCount <= BranchItemCount; itemCount++) reader.SkipItem(ref position);

                // NodeRlpReader checks a short item only as it slices it, so items filling the RLP exactly keep the reads below within it.
                if (position != end || end != rlp.Length || itemCount is not (LeafOrExtensionItemCount or BranchItemCount))
                    throw new TrieException("A proof node is neither a branch, an extension nor a leaf.");

                if (itemCount == LeafOrExtensionItemCount) _ = HexPrefix.ReadHeader(KeyHexPrefix);
            }

            public bool IsBranch => Seek(LeafOrExtensionItemCount) < _rlp.Length;

            public bool IsLeaf => !IsBranch && HexPrefix.ReadHeader(KeyHexPrefix).IsLeaf;

            public bool IsExtension => !IsBranch && !IsLeaf;

            /// <summary>The key nibbles of a leaf or an extension; empty for a branch.</summary>
            public byte[] Key => IsBranch ? [] : HexPrefix.FromBytes(KeyHexPrefix).key;

            /// <summary>Gets the hash of the child that RLP item <paramref name="index"/> references, as <see cref="TrieNode.GetChildHashAsValueKeccak"/> does.</summary>
            public bool GetChildHashAsValueKeccak(int index, out ValueHash256 keccak)
            {
                NodeRlpReader reader = new(_rlp);
                int position = Seek(index);
                bool isHash = reader.Data[position] == NodeRlpReader.KeccakRlpPrefix
                    || reader.PeekPrefixAndContentLength(position).ContentLength == ValueHash256.MemorySize;
                keccak = default;
                if (isHash) reader.DecodeValueKeccakNonNull(ref position, out keccak);
                return isHash;
            }

            /// <summary>The RLP of the child embedded as RLP item <paramref name="index"/>; empty when it is not embedded.</summary>
            public ReadOnlySpan<byte> GetInlineNodeRlp(int index)
            {
                NodeRlpReader reader = new(_rlp);
                int position = Seek(index);
                return reader.IsSequenceNext(position) ? _rlp.Slice(position, reader.PeekNextRlpLength(position)) : default;
            }

            private ReadOnlySpan<byte> KeyHexPrefix
            {
                get
                {
                    int position = Seek(0);
                    new NodeRlpReader(_rlp).DecodeByteArraySpan(ref position, out ReadOnlySpan<byte> hexPrefix);
                    return hexPrefix;
                }
            }

            /// <summary>The offset of RLP item <paramref name="index"/>.</summary>
            private int Seek(int index)
            {
                NodeRlpReader reader = new(_rlp);
                int position = 0;
                reader.ReadSequenceLength(ref position, out _);
                reader.SkipItems(ref position, index);
                return position;
            }
        }
    }
}
