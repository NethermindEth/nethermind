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
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync
{
    public static class SnapProviderHelper
    {
        private const int ExtensionRlpChildIndex = 1;

        /// <remarks>
        /// SNAP GetStorageRanges requires a proof of the origin even when no slot exists in a partial range.
        /// See https://github.com/ethereum/devp2p/blob/master/caps/snap.md#getstorageranges-0x02.
        /// </remarks>
        internal static AddRangeResult VerifyEmptyStorageRange(StorageRange request, IByteArrayList proofs)
        {
            if (request.Accounts.Count == 0) return AddRangeResult.InvalidProof;
            TreePath start = TreePath.FromPath((request.StartingHash ?? ValueKeccak.Zero).Bytes);
            TreePath limit = TreePath.FromPath((request.LimitHash ?? ValueKeccak.MaxValue).Bytes);
            if (start.CompareTo(limit) > 0) return AddRangeResult.InvalidProof;

            try
            {
                Dictionary<ValueHash256, TrieNode> nodes = CreateProofDict(proofs, preserveEncoding: true);
                if (!ValidateProofNodes(nodes, request.Accounts[0].Account.StorageRoot)) return AddRangeResult.InvalidProof;
                bool alternate = !nodes.TryGetValue(request.Accounts[0].Account.StorageRoot, out TrieNode root);
                if (alternate)
                {
                    HashSet<ValueHash256> referenced = [];
                    List<ValueHash256> children = [];
                    foreach (TrieNode node in nodes.Values)
                    {
                        children.Clear();
                        CollectProofChildren(node, children);
                        foreach (ValueHash256 hash in children) referenced.Add(hash);
                    }
                    foreach (KeyValuePair<ValueHash256, TrieNode> candidate in nodes)
                    {
                        if (referenced.Contains(candidate.Key)) continue;
                        if (root is not null) return AddRangeResult.InvalidProof;
                        root = candidate.Value;
                    }
                    if (root is null) return AddRangeResult.InvalidProof;
                    HashSet<ValueHash256> connected = [];
                    Stack<ValueHash256> graph = new();
                    graph.Push(ValueKeccak.Compute(root.FullRlp.AsSpan()));
                    while (graph.TryPop(out ValueHash256 hash))
                    {
                        if (!connected.Add(hash)) continue;
                        children.Clear();
                        CollectProofChildren(nodes[hash], children);
                        foreach (ValueHash256 childHash in children)
                            if (nodes.ContainsKey(childHash)) graph.Push(childHash);
                    }
                    if (connected.Count != nodes.Count) return AddRangeResult.InvalidProof;
                }

                if (alternate && !ValidateProofNodes(nodes, ValueKeccak.Compute(root.FullRlp.AsSpan()))) return AddRangeResult.InvalidProof;

                Stack<(TrieNode Node, TreePath Path)> pending = new();
                pending.Push((root, TreePath.Empty));
                // A proof node can appear at several paths, but the 64-nibble boundary bounds useful expansion.
                long remainingVisits = 65L * (nodes.Count + 1);
                while (pending.TryPop(out (TrieNode Node, TreePath Path) entry))
                {
                    if (--remainingVisits < 0) return AddRangeResult.InvalidProof;
                    (TrieNode node, TreePath path) = entry;
                    if (!IsCanonicalProofNode(node, nodes)) return AddRangeResult.InvalidProof;
                    if (node.IsLeaf)
                    {
                        if (path.Length + node.Key.Length != 64) return AddRangeResult.InvalidProof;
                        TreePath leaf = path.Append(node.Key);
                        if (Intersects(leaf)) return AddRangeResult.InvalidProof;
                    }
                    else if (node.IsExtension)
                    {
                        if (node.Key is not { Length: > 0 } || path.Length + node.Key.Length >= 64)
                            return AddRangeResult.InvalidProof;
                        if (!VisitChild(node, ExtensionRlpChildIndex, path.Append(node.Key))) return AddRangeResult.InvalidProof;
                    }
                    else if (node.IsBranch && path.Length < 64)
                    {
                        for (int i = 0; i < 16; i++)
                            if (!VisitChild(node, i, path.Append(i))) return AddRangeResult.InvalidProof;
                    }
                    else return AddRangeResult.InvalidProof;
                }
                return alternate ? AddRangeResult.MissingRootHashInProofs : AddRangeResult.OK;

                bool Intersects(TreePath path) => start.CompareToTruncated(path, path.Length) <= 0
                    && limit.CompareToTruncated(path, path.Length) >= 0;

                bool VisitChild(TrieNode node, int index, TreePath path)
                {
                    if (!Intersects(path)) return true;
                    TrieNode? child;
                    if (node.GetChildHashAsValueKeccak(index, out ValueHash256 hash))
                    {
                        if (!nodes.TryGetValue(hash, out child)) return false;
                    }
                    else if (node.GetInlineNodeRlp(index) is { } rlp)
                    {
                        child = new TrieNode(NodeType.Unknown, rlp);
                        child.ResolveNode(NullTrieNodeResolver.Instance, path);
                    }
                    else return !node.IsExtension;

                    pending.Push((child, path));
                    return true;
                }
            }
            catch (Exception exception) when (IsInvalidProofException(exception))
            {
                return AddRangeResult.InvalidProof;
            }
        }

        private static void CollectProofChildren(TrieNode node, List<ValueHash256> children)
        {
            int count = node.IsBranch ? 16 : node.IsExtension ? 2 : 0;
            for (int i = node.IsExtension ? ExtensionRlpChildIndex : 0; i < count; i++)
            {
                if (node.GetChildHashAsValueKeccak(i, out ValueHash256 hash)) children.Add(hash);
                else if (node.GetInlineNodeRlp(i) is { } rlp)
                {
                    children.Add(ValueKeccak.Compute(rlp));
                    TrieNode child = new(NodeType.Unknown, rlp);
                    child.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
                    CollectProofChildren(child, children);
                }
            }
        }

        private static bool ValidateProofNodes(Dictionary<ValueHash256, TrieNode> nodes, ValueHash256? rootedHash = null)
        {
            HashSet<ValueHash256> expanded = [];
            Queue<ValueHash256> discovered = new(nodes.Keys);
            while (discovered.TryDequeue(out ValueHash256 hash))
            {
                if (!expanded.Add(hash)) continue;
                TrieNode node = nodes[hash];
                if (!IsCanonicalProofNode(node, nodes)) return false;
                int count = node.IsBranch ? 16 : node.IsExtension ? 2 : 0;
                for (int i = node.IsExtension ? ExtensionRlpChildIndex : 0; i < count; i++)
                {
                    if (node.GetChildHashAsValueKeccak(i, out _)) continue;
                    if (node.GetInlineNodeRlp(i) is not { } rlp) continue;
                    ValueHash256 childHash = ValueKeccak.Compute(rlp);
                    if (nodes.ContainsKey(childHash)) continue;
                    TrieNode child = new(NodeType.Unknown, rlp, isDirty: false);
                    child.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
                    nodes.Add(childHash, child);
                    discovered.Enqueue(childHash);
                }
            }

            Dictionary<ValueHash256, List<ValueHash256>> edges = [];
            foreach (KeyValuePair<ValueHash256, TrieNode> entry in nodes)
            {
                TrieNode node = entry.Value;
                if (!IsCanonicalProofNode(node, nodes)) return false;
                List<ValueHash256> children = [];
                edges.Add(entry.Key, children);
                int count = node.IsBranch ? 16 : node.IsExtension ? 2 : 0;
                for (int i = node.IsExtension ? ExtensionRlpChildIndex : 0; i < count; i++)
                {
                    if (node.GetChildHashAsValueKeccak(i, out ValueHash256 childHash))
                    {
                        if (nodes.ContainsKey(childHash)) children.Add(childHash);
                    }
                    else if (node.GetInlineNodeRlp(i) is { } rlp) children.Add(ValueKeccak.Compute(rlp));
                }
            }

            Dictionary<ValueHash256, int> depths = [];
            Queue<(ValueHash256 Hash, int Depth)> pending = new();
            foreach (ValueHash256 hash in nodes.Keys)
            {
                depths.Add(hash, 0);
                pending.Enqueue((hash, 0));
            }
            // Detached nodes may begin deeper; known edges establish a lower bound without assuming a root.
            long remainingVisits = 65L * nodes.Count;
            while (pending.TryDequeue(out (ValueHash256 Hash, int Depth) entry))
            {
                if (--remainingVisits < 0) return false;
                if (depths[entry.Hash] != entry.Depth) continue;
                TrieNode node = nodes[entry.Hash];
                int end = entry.Depth + (node.IsBranch ? 1 : node.Key.Length);
                if (node.IsLeaf && end > 64 || node.IsExtension && end >= 64 || node.IsBranch && entry.Depth >= 64) return false;
                foreach (ValueHash256 childHash in edges[entry.Hash])
                {
                    if (end <= depths[childHash]) continue;
                    depths[childHash] = end;
                    pending.Enqueue((childHash, end));
                }
            }
            if (rootedHash is { } root && nodes.ContainsKey(root))
            {
                HashSet<(ValueHash256 Hash, int Depth)> visited = [];
                pending.Enqueue((root, 0));
                remainingVisits = 65L * nodes.Count;
                while (pending.TryDequeue(out (ValueHash256 Hash, int Depth) entry))
                {
                    if (!visited.Add(entry)) continue;
                    if (--remainingVisits < 0) return false;
                    TrieNode node = nodes[entry.Hash];
                    int end = entry.Depth + (node.IsBranch ? 1 : node.Key.Length);
                    if (node.IsLeaf && end != 64 || node.IsExtension && end >= 64 || node.IsBranch && entry.Depth >= 64) return false;
                    foreach (ValueHash256 childHash in edges[entry.Hash]) pending.Enqueue((childHash, end));
                }
            }
            return true;
        }

        private static bool IsCanonicalProofNode(TrieNode node, Dictionary<ValueHash256, TrieNode> nodes)
        {
            RlpReader encoded = new(node.FullRlp.AsSpan());
            RlpReader reader = encoded.EnterSequence(out int itemLength);
            if (itemLength != encoded.Remaining) return false;
            if (node.IsBranch)
            {
                int children = 0;
                for (int i = 0; i < 16; i++)
                {
                    if (!ReadCanonicalChild(ref reader, nodes, out _, out bool empty)) return false;
                    if (!empty) children++;
                }
                if (children < 2 || !reader.ReadBytes().IsEmpty) return false;
            }
            else
            {
                ReadOnlySpan<byte> key = reader.ReadBytes();
                if (key.IsEmpty || key[0] >> 4 > 3 || (key[0] & 0x10) == 0 && (key[0] & 0x0f) != 0) return false;
                int pathLength = (key.Length - 1) * 2 + ((key[0] & 0x10) != 0 ? 1 : 0);
                if (node.IsLeaf)
                {
                    if (pathLength > 64 || (key[0] & 0x20) == 0 || reader.ReadBytes().IsEmpty) return false;
                }
                else if (node.IsExtension)
                {
                    if ((key[0] & 0x20) != 0 || pathLength is 0 or >= 64 ||
                        !ReadCanonicalChild(ref reader, nodes, out TrieNode? child, out bool empty) || empty ||
                        child is not null && !child.IsBranch) return false;
                }
                else return false;
            }
            return !reader.HasNext;
        }

        private static bool ReadCanonicalChild(ref RlpReader reader, Dictionary<ValueHash256, TrieNode> nodes,
            out TrieNode? child, out bool empty)
        {
            child = null;
            empty = false;
            if (reader.IsNextSequence)
            {
                ReadOnlySpan<byte> encoded = reader.ReadEncodedItem();
                if (encoded.Length >= 32) return false;
                child = new TrieNode(NodeType.Unknown, encoded.ToArray());
                child.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
                return IsCanonicalProofNode(child, nodes);
            }
            ReadOnlySpan<byte> value = reader.ReadBytes();
            if (value.IsEmpty)
            {
                empty = true;
                return true;
            }
            if (value.Length != 32) return false;
            if (nodes.TryGetValue(new ValueHash256(value), out child) && child.FullRlp.Length < 32) return false;
            return true;
        }

        internal static AddRangeResult VerifyStorageRange(ISnapTrieFactory factory, PathWithAccount account,
            IReadOnlyList<PathWithStorageSlot> slots, ValueHash256? startingHash, ValueHash256? limitHash, IByteArrayList? proofs)
        {
            using ISnapTree<PathWithStorageSlot> tree = factory.CreateStorageTree(account.Path);
            return BuildAndVerifyRoot(tree, slots, startingHash ?? ValueKeccak.Zero,
                limitHash ?? ValueKeccak.MaxValue, account.Account.StorageRoot, proofs).result;
        }

        internal static AddRangeResult VerifyAlternateStorageRange(ISnapTrieFactory factory, ValueHash256 accountPath,
            IReadOnlyList<PathWithStorageSlot> slots, ValueHash256? startingHash, ValueHash256? limitHash, IByteArrayList? proofs)
        {
            if (proofs is not { Count: > 0 }) return AddRangeResult.InvalidProof;
            try
            {
                Dictionary<ValueHash256, TrieNode> nodes = CreateProofDict(proofs, preserveEncoding: true);
                if (!ValidateProofNodes(nodes)) return AddRangeResult.InvalidProof;
                HashSet<ValueHash256> referenced = [];
                List<ValueHash256> children = [];
                foreach (TrieNode node in nodes.Values)
                {
                    children.Clear();
                    CollectProofChildren(node, children);
                    foreach (ValueHash256 child in children) referenced.Add(child);
                }
                ValueHash256? root = null;
                foreach (ValueHash256 candidate in nodes.Keys)
                {
                    if (referenced.Contains(candidate)) continue;
                    if (root is not null) return AddRangeResult.InvalidProof;
                    root = candidate;
                }
                if (root is not { } candidateRoot || !ValidateProofNodes(nodes, candidateRoot)) return AddRangeResult.InvalidProof;
                HashSet<ValueHash256> connected = [];
                Stack<ValueHash256> pending = new();
                pending.Push(candidateRoot);
                while (pending.TryPop(out ValueHash256 hash))
                {
                    if (!connected.Add(hash)) continue;
                    children.Clear();
                    CollectProofChildren(nodes[hash], children);
                    foreach (ValueHash256 child in children)
                        if (nodes.ContainsKey(child)) pending.Push(child);
                }
                if (connected.Count != nodes.Count) return AddRangeResult.InvalidProof;
                using ISnapTree<PathWithStorageSlot> tree = factory.CreateStorageTree(accountPath);
                return BuildAndVerifyRoot(tree, slots, startingHash ?? ValueKeccak.Zero,
                    limitHash ?? ValueKeccak.MaxValue, candidateRoot, proofs).result;
            }
            catch (Exception exception) when (IsInvalidProofException(exception))
            {
                return AddRangeResult.InvalidProof;
            }
        }

        public static (AddRangeResult result, bool moreChildrenToRight, List<PathWithAccount> storageRoots, List<ValueHash256> codeHashes, Hash256 actualRootHash) AddAccountRange(
            ISnapTrieFactory factory,
            ulong blockNumber,
            in ValueHash256 expectedRootHash,
            in ValueHash256 startingHash,
            in ValueHash256 limitHash,
            IReadOnlyList<PathWithAccount> accounts,
            IByteArrayList proofs = null,
            ValueHash256? partitionStart = null
        )
        {
            using ISnapTree<PathWithAccount> tree = factory.CreateStateTree();

            (AddRangeResult result, bool moreChildrenToRight, _) = CommitRange(
                tree, accounts, startingHash, limitHash, expectedRootHash, proofs, partitionStart ?? ValueKeccak.Zero);
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

            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].SlotRlpValue.Length == 0)
                    return (AddRangeResult.InvalidProof, true, tree.RootHash, false);
            }

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
            IByteArrayList? proofs,
            ValueHash256? accountPartitionStart = null) where TEntry : ISnapEntry
        {
            (AddRangeResult result, List<(TrieNode, TreePath)>? sortedBoundaryList, bool moreChildrenToRight) =
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

            StitchBoundaries(sortedBoundaryList, tree, startingHash);
            ValueHash256 cleanupEnd = accountPartitionStart is not null && limitHash < upperBound ? limitHash : upperBound;
            ValueHash256 cleanupStart = accountPartitionStart ?? ValueKeccak.Zero;
            if (sortedBoundaryList is null)
                tree.DeleteOutsideCompleteTree(cleanupStart, cleanupEnd);
            else
                DeleteOutsideChildren(sortedBoundaryList, tree, upperBound, cleanupStart, cleanupEnd);

            tree.Commit(upperBound);

            bool isRootPersisted = sortedBoundaryList is not { Count: > 0 } || sortedBoundaryList[0].Item1.IsPersisted;
            return (AddRangeResult.OK, moreChildrenToRight, isRootPersisted);
        }

        /// <summary>
        /// Fills the boundary proof nodes and bulk-sets the range entries into <paramref name="tree"/> in memory,
        /// then checks that the recomputed root equals <paramref name="expectedRootHash"/>. Nothing is committed.
        /// </summary>
        private static (AddRangeResult result, List<(TrieNode, TreePath)>? sortedBoundaryList, bool moreChildrenToRight) BuildAndVerifyRoot<TEntry>(
            ISnapTree<TEntry> tree,
            IReadOnlyList<TEntry> entries,
            in ValueHash256 startingHash,
            in ValueHash256 limitHash,
            in ValueHash256 expectedRootHash,
            IByteArrayList? proofs) where TEntry : ISnapEntry
        {
            if (typeof(TEntry) == typeof(PathWithStorageSlot) && proofs is { Count: > 0 })
            {
                try
                {
                    Dictionary<ValueHash256, TrieNode> originalNodes = CreateProofDict(proofs, preserveEncoding: true);
                    if (!ValidateProofNodes(originalNodes, expectedRootHash)) return (AddRangeResult.InvalidProof, null, false);
                }
                catch (Exception exception) when (IsInvalidProofException(exception))
                {
                    return (AddRangeResult.InvalidProof, null, false);
                }
            }

            // Without entries a proof is a proof of absence: on its own it has to rebuild the root with all of
            // [startingHash, limitHash] cleared, which leaves nothing more to fetch in the range.
            bool isProofOfAbsence = entries.Count == 0;
            if (isProofOfAbsence && proofs is not { Count: > 0 })
                return (AddRangeResult.EmptyRange, null, false);

            // Validate sorting order
            for (int i = 1; i < entries.Count; i++)
            {
                if (entries[i - 1].Path.CompareTo(entries[i].Path) >= 0)
                    return (AddRangeResult.InvalidOrder, null, false);
            }

            if (!isProofOfAbsence && entries[0].Path < startingHash)
                return (AddRangeResult.InvalidOrder, null, false);

            ValueHash256 lastPath = isProofOfAbsence ? limitHash : entries[entries.Count - 1].Path;

            AddRangeResult result;
            List<(TrieNode, TreePath)> sortedBoundaryList;
            bool moreChildrenToRight;
            try
            {
                (result, sortedBoundaryList, moreChildrenToRight) =
                    FillBoundaryTree(tree, startingHash, lastPath, limitHash, expectedRootHash, proofs);
                if (result == AddRangeResult.OK) tree.BulkSetAndUpdateRootHash(entries);
            }
            catch (Exception exception) when (IsInvalidProofException(exception))
            {
                return (AddRangeResult.InvalidProof, null, false);
            }

            if (result != AddRangeResult.OK)
                return (result, null, false);

            if (tree.RootHash.ValueHash256 != expectedRootHash)
                return (AddRangeResult.DifferentRootHash, null, false);

            return (AddRangeResult.OK, sortedBoundaryList, moreChildrenToRight && !isProofOfAbsence);
        }

        private static bool IsInvalidProofException(Exception exception)
        {
            if (exception is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                    if (!IsInvalidProofException(inner)) return false;
                return true;
            }
            return exception is TrieException or RlpReaderException;
        }

        [SkipLocalsInit]
        private static (AddRangeResult result, List<(TrieNode, TreePath)> sortedBoundaryList, bool moreChildrenToRight) FillBoundaryTree<TEntry>(
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
                return (AddRangeResult.OK, null, false);
            }

            ArgumentNullException.ThrowIfNull(tree);

            ValueHash256 effectiveStartingHash = startingHash ?? ValueKeccak.Zero;
            List<(TrieNode, TreePath)> sortedBoundaryList = [];

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
            long remainingVisits = 65L * (dict.Count + 1);
            while (proofNodesToProcess.Count > 0)
            {
                if (--remainingVisits < 0) return (AddRangeResult.InvalidProof, null, true);
                (TrieNode node, TreePath path) = proofNodesToProcess.Pop();

                if (node.IsExtension)
                {
                    if (node.Key is not { Length: > 0 })
                    {
                        return (AddRangeResult.InvalidProofNode, null, true);
                    }

                    if (node.GetChildHashAsValueKeccak(ExtensionRlpChildIndex, out ValueHash256 childKeccak))
                    {
                        TreePath childPath = path.Append(node.Key);

                        moreChildrenToRight |= childPath.Path > limitHash;

                        if (childPath.CompareTo(leftBoundaryPath.Truncate(childPath.Length)) >= 0)
                        {
                            if (dict.TryGetValue(childKeccak, out TrieNode child))
                            {
                                node.SetChild(0, child);

                                proofNodesToProcess.Push((child, childPath));
                                sortedBoundaryList.Add((child, childPath));
                            }
                            else if (childPath.CompareTo(rightBoundaryPath.Truncate(childPath.Length)) <= 0)
                            {
                                // The child's keys overlap [start, end]: the path of the start or of the end runs through
                                // this extension, or it is a root extension inside the range. Kept as a hash, the child
                                // would stand for every key of the range under it: the entries could leave them out and
                                // the root would still match.
                                return (AddRangeResult.InvalidProof, null, true);
                            }
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
                                    // Leaf are range proof, proof that the range covers the requested path.
                                    // But we dont persist them as it should be persisted by the range itself through
                                    // other range.
                                    node.NodeData[ci] = child.Keccak;
                                }
                            }
                        }
                    }
                }
            }

            return (AddRangeResult.OK, sortedBoundaryList, moreChildrenToRight);
        }

        private static Dictionary<ValueHash256, TrieNode> CreateProofDict(IByteArrayList proofs, bool preserveEncoding = false)
        {
            Dictionary<ValueHash256, TrieNode> dict = [];

            for (int i = 0; i < proofs.Count; i++)
            {
                byte[] proof = proofs[i].ToArray();
                TrieNode node = new(NodeType.Unknown, proof, isDirty: !preserveEncoding);
                node.IsBoundaryProofNode = true;

                TreePath emptyPath = TreePath.Empty;
                node.ResolveNode(UnknownNodeResolver.Instance, emptyPath);
                node.ResolveKey(UnknownNodeResolver.Instance, ref emptyPath);

                dict[node.Keccak] = node;
            }

            return dict;
        }

        private static void StitchBoundaries<TEntry>(List<(TrieNode, TreePath)>? sortedBoundaryList, ISnapTree<TEntry> tree, ValueHash256 startPath) where TEntry : ISnapEntry
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

        private static void DeleteOutsideChildren<TEntry>(List<(TrieNode, TreePath)>? sortedBoundaryList, ISnapTree<TEntry> tree, in ValueHash256 upperBound, in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd) where TEntry : ISnapEntry
        {
            if (sortedBoundaryList is null) return;

            foreach ((TrieNode node, TreePath path) in sortedBoundaryList)
            {
                if (CanDeleteOutsideChildren(node, path, upperBound))
                {
                    tree.DeleteOutsideChildren(path, node, cleanupStart, cleanupEnd);
                }
            }
        }

        internal static bool CanDeleteOutsideChildren(TrieNode node, in TreePath path, in ValueHash256 upperBound) =>
            !node.IsPersisted && !node.IsBoundaryProofNode && (node.IsBranch || node.IsExtension)
            && SnapUpperBoundAdapter.IsWithinUpperBound(node, path, upperBound);

        private static bool IsChildPersisted<TEntry>(TrieNode node, ref TreePath nodePath, object? child, int childIndex, ISnapTree<TEntry> tree, ValueHash256 startPath) where TEntry : ISnapEntry
        {
            if (child is TrieNode childNode)
            {
                return !childNode.IsBoundaryProofNode;
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
