// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Atomically applies complete-key mutations to a canonical compressed EIP-8297 tree.</summary>
public static class TrieUpdater
{
    private static readonly PbtNodePath RootPath = new([], 0);

    /// <summary>Applies <paramref name="changes"/> and returns the resulting canonical root.</summary>
    /// <remarks>
    /// Effective mutations are folded through the tree as traversal-local partitioned ranges, so mutations
    /// sharing a path share one traversal. All leaf and node writes are staged and handed to
    /// <see cref="IPbtStore.Apply"/> only after the complete mutation succeeds.
    /// </remarks>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch changes) =>
        UpdateRoot(store, currentRoot, changes, null);

    internal static ValueHash256 UpdateRoot(
        IPbtStore store,
        in ValueHash256 currentRoot,
        PbtWriteBatch changes,
        TrieUpdaterMetrics? metrics)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0) return currentRoot;

        // PbtWriteBatch supplies unique keys with deletions preceding writes.
        PbtWriteOperation[] operations = [.. changes.Operations];

        GroupOverlay overlay = new(store, metrics);
        return FoldMutations(store, overlay, null, RootPath, currentRoot, operations.AsSpan());
    }

    private static ValueHash256 FoldMutations(
        IPbtStore store,
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        in ValueHash256 expectedHash,
        Span<PbtWriteOperation> operations)
    {
        if (expectedHash == default)
        {
            int setCount = RetainSets(operations);
            return setCount == 0 ? default : BuildSubtree(overlay, activeGroup, path, operations[..setCount]);
        }

        if (activeGroup is not null && activeGroup.TryGetPosition(path, out _))
            return FoldMutationsInGroup(store, overlay, activeGroup, path, expectedHash, operations);

        using GroupFrame group = overlay.Resolve(null, path, out _);
        if (operations.Length > 1)
            BucketizeByGroupBoundary(operations, group.BitDepth);
        return FoldMutationsInGroup(store, overlay, group, path, expectedHash, operations);
    }

    private static ValueHash256 FoldMutationsInGroup(
        IPbtStore store,
        GroupOverlay overlay,
        GroupFrame group,
        PbtNodePath path,
        in ValueHash256 expectedHash,
        Span<PbtWriteOperation> operations)
    {
        if (expectedHash == default)
        {
            int setCount = RetainSets(operations);
            return setCount == 0 ? default : BuildSubtreeInGroup(overlay, group, path, operations[..setCount]);
        }

        PbtNode current = overlay.Load(group, path, expectedHash, out group);
        if (current is PbtLeafNode leaf)
        {
            PbtNode? surviving = leaf;
            for (int index = 0; index < operations.Length; index++)
            {
                PbtWriteOperation operation = operations[index];
                if (!operation.Key.Equals(leaf.Key)) continue;
                if (operation.Kind == PbtWriteOperationKind.Delete)
                {
                    store.SetLeaf(leaf.Key, null);
                    surviving = null;
                }
                else
                {
                    store.SetLeaf(operation.Key, operation.Value);
                    surviving = new PbtLeafNode(operation.Key, operation.Value.Bytes.ToArray());
                }
                break;
            }

            int setCount = 0;
            for (int index = 0; index < operations.Length; index++)
            {
                PbtWriteOperation operation = operations[index];
                if (operation.Kind == PbtWriteOperationKind.Delete || operation.Key.Equals(leaf.Key)) continue;
                (operations[setCount], operations[index]) = (operations[index], operations[setCount]);
                setCount++;
            }

            if (surviving is null)
            {
                overlay.Remove(group, path);
                return setCount == 0 ? default : BuildSubtree(overlay, group, path, operations[..setCount]);
            }
            if (setCount == 0)
            {
                if (surviving.Hash != leaf.Hash) overlay.Store(group, path, surviving);
                return surviving.Hash;
            }
            return InsertSetsIntoNode(overlay, group, path, surviving, operations[..setCount]);
        }

        PbtBranchNode branch = (PbtBranchNode)current;
        int matchingCount = FindMatchingBranchRange(branch, path.BitDepth, operations);
        Span<PbtWriteOperation> matchingOperations = operations[..matchingCount];
        int partition = PartitionByBit(matchingOperations, path.BitDepth + branch.Prefix.BitCount);
        PbtNodePath leftPath = path.Append(branch.Prefix, 0);
        PbtNodePath rightPath = path.Append(branch.Prefix, 1);
        ValueHash256 leftHash = branch.LeftHash;
        ValueHash256 rightHash = branch.RightHash;
        if (partition > 0)
            leftHash = FoldMutations(store, overlay, group, leftPath, leftHash, matchingOperations[..partition]);
        if (partition < matchingOperations.Length)
            rightHash = FoldMutations(store, overlay, group, rightPath, rightHash, matchingOperations[partition..]);

        ValueHash256 reconciledHash = CanonicalizeBranch(overlay, group, path, branch.Prefix, leftPath, leftHash, rightPath, rightHash);
        int divergentSetCount = RetainSets(operations[matchingCount..]);
        Span<PbtWriteOperation> divergentSets = operations.Slice(matchingCount, divergentSetCount);
        if (divergentSets.Length == 0) return reconciledHash;
        if (reconciledHash == default)
            return BuildSubtree(overlay, group, path, divergentSets);
        PbtNode reconciled = overlay.Load(group, path, reconciledHash, out GroupFrame reconciledGroup);
        bool ownsReconciledGroup = !ReferenceEquals(reconciledGroup, group);
        try
        {
            return InsertSetsIntoNode(overlay, reconciledGroup, path, reconciled, divergentSets);
        }
        finally
        {
            if (ownsReconciledGroup) reconciledGroup.Dispose();
        }
    }

    private static int RetainSets(Span<PbtWriteOperation> operations)
    {
        int setCount = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            if (operations[index].Kind == PbtWriteOperationKind.Delete) continue;
            (operations[setCount], operations[index]) = (operations[index], operations[setCount]);
            setCount++;
        }
        return setCount;
    }

    private static ValueHash256 CanonicalizeBranch(
        GroupOverlay overlay,
        GroupFrame group,
        PbtNodePath path,
        PbtBitPrefix prefix,
        PbtNodePath leftPath,
        in ValueHash256 leftHash,
        PbtNodePath rightPath,
        in ValueHash256 rightHash)
    {
        if (leftHash != default && rightHash != default)
        {
            PbtBranchNode replacement = new(prefix, leftHash, rightHash);
            overlay.Store(group, path, replacement);
            return replacement.Hash;
        }
        if (leftHash == default && rightHash == default)
        {
            overlay.Remove(group, path);
            return default;
        }

        int remainingDirection = leftHash != default ? 0 : 1;
        PbtNodePath remainingPath = remainingDirection == 0 ? leftPath : rightPath;
        ValueHash256 remainingHash = remainingDirection == 0 ? leftHash : rightHash;
        PbtNode remaining = overlay.Load(group, remainingPath, remainingHash, out GroupFrame remainingGroup);
        bool ownsRemainingGroup = !ReferenceEquals(remainingGroup, group);
        try
        {
            overlay.Remove(remainingGroup, remainingPath);
            PbtNode promoted = remaining is PbtBranchNode remainingBranch
                ? new PbtBranchNode(PbtBitPrefix.Concat(prefix, remainingDirection, remainingBranch.Prefix), remainingBranch.LeftHash, remainingBranch.RightHash)
                : remaining;
            overlay.Store(group, path, promoted);
            return promoted.Hash;
        }
        finally
        {
            if (ownsRemainingGroup) remainingGroup.Dispose();
        }
    }

    private static ValueHash256 InsertSets(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        in ValueHash256 expectedHash,
        Span<PbtWriteOperation> sets)
    {
        if (expectedHash == default) return BuildSubtree(overlay, activeGroup, path, sets);

        if (activeGroup is not null && activeGroup.TryGetPosition(path, out _))
            return InsertSetsInGroup(overlay, activeGroup, path, expectedHash, sets);

        using GroupFrame group = overlay.Resolve(null, path, out _);
        if (sets.Length > 1)
            BucketizeByGroupBoundary(sets, group.BitDepth);
        return InsertSetsInGroup(overlay, group, path, expectedHash, sets);
    }

    private static ValueHash256 InsertSetsInGroup(
        GroupOverlay overlay,
        GroupFrame group,
        PbtNodePath path,
        in ValueHash256 expectedHash,
        Span<PbtWriteOperation> sets)
    {
        PbtNode current = overlay.Load(group, path, expectedHash, out group);
        return InsertSetsIntoNode(overlay, group, path, current, sets);
    }

    private static ValueHash256 InsertSetsIntoNode(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        PbtNode current,
        Span<PbtWriteOperation> sets) => current switch
        {
            PbtLeafNode leaf => InsertSetsIntoLeaf(overlay, activeGroup, path, leaf, sets),
            PbtBranchNode branch => InsertSetsIntoBranch(overlay, activeGroup, path, branch, sets),
            _ => throw new ArgumentOutOfRangeException(nameof(current)),
        };

    private static ValueHash256 InsertSetsIntoLeaf(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        PbtLeafNode leaf,
        Span<PbtWriteOperation> sets)
    {
        GroupFrame group = overlay.Resolve(activeGroup, path, out _);
        int differingBit = int.MaxValue;
        for (int index = 0; index < sets.Length; index++)
        {
            PbtFullKey key = sets[index].Key;
            if (leaf.Key.Equals(key)) continue;
            int difference = leaf.Key.FirstDifferingBit(key, path.BitDepth);
            if (difference == Math.Min(leaf.Key.BitLength, key.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(key));
            differingBit = Math.Min(differingBit, difference);
        }

        if (differingBit == int.MaxValue)
        {
            overlay.SetLeaf(sets[0]);
            PbtLeafNode replacement = new(sets[0].Key, sets[0].Value.Bytes.ToArray());
            overlay.Store(group, path, replacement);
            return replacement.Hash;
        }

        PbtBitPrefix common = PbtBitPrefix.FromKey(leaf.Key, path.BitDepth, differingBit - path.BitDepth);
        int existingDirection = leaf.Key.GetBit(differingBit);
        int partition = PartitionByBit(sets, differingBit);
        PbtNodePath leftPath = path.Append(common, 0);
        PbtNodePath rightPath = path.Append(common, 1);
        PbtNodePath existingPath = existingDirection == 0 ? leftPath : rightPath;
        GroupFrame existingGroup = overlay.Store(group, existingPath, leaf);
        bool ownsExistingGroup = !ReferenceEquals(existingGroup, group);
        try
        {
            ValueHash256 leftHash;
            ValueHash256 rightHash;
            if (existingDirection == 0)
            {
                leftHash = partition > 0
                    ? InsertSetsIntoNode(overlay, existingGroup, leftPath, leaf, sets[..partition])
                    : leaf.Hash;
                rightHash = BuildSubtree(overlay, group, rightPath, sets[partition..]);
            }
            else
            {
                leftHash = BuildSubtree(overlay, group, leftPath, sets[..partition]);
                rightHash = partition < sets.Length
                    ? InsertSetsIntoNode(overlay, existingGroup, rightPath, leaf, sets[partition..])
                    : leaf.Hash;
            }

            PbtBranchNode split = new(common, leftHash, rightHash);
            overlay.Store(group, path, split);
            return split.Hash;
        }
        finally
        {
            if (ownsExistingGroup) existingGroup.Dispose();
        }
    }

    private static ValueHash256 InsertSetsIntoBranch(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        PbtBranchNode branch,
        Span<PbtWriteOperation> sets)
    {
        GroupFrame group = overlay.Resolve(activeGroup, path, out _);
        int firstMismatch = branch.Prefix.BitCount;
        for (int index = 0; index < sets.Length; index++)
        {
            PbtFullKey key = sets[index].Key;
            int available = key.BitLength - path.BitDepth;
            int matched = MatchingPrefixBits(branch.Prefix, key, path.BitDepth);
            if (matched == available)
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(key));
            firstMismatch = Math.Min(firstMismatch, matched);
        }

        if (firstMismatch < branch.Prefix.BitCount)
        {
            PbtBitPrefix common = PrefixSlice(branch.Prefix, 0, firstMismatch);
            int existingDirection = branch.Prefix.GetBit(firstMismatch);
            PbtBranchNode relocated = new(
                PrefixSlice(branch.Prefix, firstMismatch + 1, branch.Prefix.BitCount - firstMismatch - 1),
                branch.LeftHash,
                branch.RightHash);
            int directionBit = path.BitDepth + firstMismatch;
            int partition = PartitionByBit(sets, directionBit);
            PbtNodePath leftPath = path.Append(common, 0);
            PbtNodePath rightPath = path.Append(common, 1);
            PbtNodePath existingPath = existingDirection == 0 ? leftPath : rightPath;
            GroupFrame existingGroup = overlay.Store(group, existingPath, relocated);
            bool ownsExistingGroup = !ReferenceEquals(existingGroup, group);
            try
            {
                ValueHash256 leftHash;
                ValueHash256 rightHash;
                if (existingDirection == 0)
                {
                    leftHash = partition > 0
                        ? InsertSetsIntoNode(overlay, existingGroup, leftPath, relocated, sets[..partition])
                        : relocated.Hash;
                    rightHash = BuildSubtree(overlay, group, rightPath, sets[partition..]);
                }
                else
                {
                    leftHash = BuildSubtree(overlay, group, leftPath, sets[..partition]);
                    rightHash = partition < sets.Length
                        ? InsertSetsIntoNode(overlay, existingGroup, rightPath, relocated, sets[partition..])
                        : relocated.Hash;
                }

                PbtBranchNode split = new(common, leftHash, rightHash);
                overlay.Store(group, path, split);
                return split.Hash;
            }
            finally
            {
                if (ownsExistingGroup) existingGroup.Dispose();
            }
        }

        int childDirectionBit = path.BitDepth + branch.Prefix.BitCount;
        int childPartition = PartitionByBit(sets, childDirectionBit);
        ValueHash256 replacementLeft = branch.LeftHash;
        ValueHash256 replacementRight = branch.RightHash;
        if (childPartition > 0)
        {
            PbtNodePath leftPath = path.Append(branch.Prefix, 0);
            replacementLeft = InsertSets(overlay, group, leftPath, replacementLeft, sets[..childPartition]);
        }
        if (childPartition < sets.Length)
        {
            PbtNodePath rightPath = path.Append(branch.Prefix, 1);
            replacementRight = InsertSets(overlay, group, rightPath, replacementRight, sets[childPartition..]);
        }

        PbtBranchNode replacement = new(branch.Prefix, replacementLeft, replacementRight);
        overlay.Store(group, path, replacement);
        return replacement.Hash;
    }

    private static ValueHash256 BuildSubtree(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        Span<PbtWriteOperation> sets)
    {
        if (activeGroup is not null && activeGroup.TryGetPosition(path, out _))
            return BuildSubtreeInGroup(overlay, activeGroup, path, sets);

        using GroupFrame group = overlay.Resolve(null, path, out _);
        if (sets.Length > 1)
            BucketizeByGroupBoundary(sets, group.BitDepth);
        return BuildSubtreeInGroup(overlay, group, path, sets);
    }

    private static ValueHash256 BuildSubtreeInGroup(
        GroupOverlay overlay,
        GroupFrame group,
        PbtNodePath path,
        Span<PbtWriteOperation> sets)
    {
        if (sets.Length == 1)
        {
            PbtWriteOperation operation = sets[0];
            overlay.SetLeaf(operation);
            PbtLeafNode leaf = new(operation.Key, operation.Value.Bytes.ToArray());
            overlay.Store(group, path, leaf);
            return leaf.Hash;
        }

        PbtFullKey firstKey = sets[0].Key;
        int differingBit = int.MaxValue;
        for (int index = 1; index < sets.Length; index++)
        {
            PbtFullKey key = sets[index].Key;
            int difference = firstKey.FirstDifferingBit(key, path.BitDepth);
            if (difference == Math.Min(firstKey.BitLength, key.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(sets));
            differingBit = Math.Min(differingBit, difference);
        }
        PbtBitPrefix prefix = PbtBitPrefix.FromKey(firstKey, path.BitDepth, differingBit - path.BitDepth);
        int partition = PartitionByBit(sets, differingBit);
        PbtNodePath leftPath = path.Append(prefix, 0);
        PbtNodePath rightPath = path.Append(prefix, 1);
        ValueHash256 leftHash = BuildSubtree(overlay, group, leftPath, sets[..partition]);
        ValueHash256 rightHash = BuildSubtree(overlay, group, rightPath, sets[partition..]);
        PbtBranchNode branch = new(prefix, leftHash, rightHash);
        overlay.Store(group, path, branch);
        return branch.Hash;
    }

    private static int FindMatchingBranchRange(
        PbtBranchNode branch,
        int bitDepth,
        Span<PbtWriteOperation> operations)
    {
        int matchingCount = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            PbtFullKey key = operations[index].Key;
            int available = key.BitLength - bitDepth;
            if (available <= branch.Prefix.BitCount ||
                MatchingPrefixBits(branch.Prefix, key, bitDepth) != branch.Prefix.BitCount)
                continue;

            (operations[matchingCount], operations[index]) = (operations[index], operations[matchingCount]);
            matchingCount++;
        }
        return matchingCount;
    }

    private static int PartitionByBit(Span<PbtWriteOperation> operations, int bitIndex)
    {
        int partition = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            if (operations[index].Key.GetBit(bitIndex) != 0) continue;
            (operations[partition], operations[index]) = (operations[index], operations[partition]);
            partition++;
        }
        return partition;
    }

    private static void BucketizeByGroupBoundary(Span<PbtWriteOperation> operations, int groupDepth)
    {
        Span<int> starts = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        Span<int> counts = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        for (int index = 0; index < operations.Length; index++)
            counts[BoundarySlot(operations[index].Key, groupDepth)]++;

        int total = 0;
        for (int bucket = 0; bucket < PbtFourLevelGroupGeometry.BoundarySlots; bucket++)
        {
            starts[bucket] = total;
            total += counts[bucket];
        }

        Span<int> next = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        starts.CopyTo(next);
        for (int bucket = 0; bucket < PbtFourLevelGroupGeometry.BoundarySlots; bucket++)
        {
            int end = starts[bucket] + counts[bucket];
            while (next[bucket] < end)
            {
                int index = next[bucket];
                int destination = BoundarySlot(operations[index].Key, groupDepth);
                if (destination == bucket)
                {
                    next[bucket]++;
                    continue;
                }

                (operations[index], operations[next[destination]]) =
                    (operations[next[destination]], operations[index]);
                next[destination]++;
            }
        }
    }

    private static int BoundarySlot(PbtFullKey key, int groupDepth)
    {
        int slot = 0;
        for (int level = 0; level < PbtFourLevelGroupGeometry.LevelsPerGroup; level++)
            slot = (slot << 1) | key.GetBit(groupDepth + level);
        return slot;
    }

    private static int MatchingPrefixBits(PbtBitPrefix prefix, PbtFullKey key, int keyOffset)
    {
        int available = key.BitLength - keyOffset;
        int count = Math.Min(prefix.BitCount, available);
        int index = 0;
        while (index < count && prefix.GetBit(index) == key.GetBit(keyOffset + index)) index++;
        return index;
    }

    private static PbtBitPrefix PrefixSlice(PbtBitPrefix prefix, int start, int count)
    {
        byte[] bytes = new byte[PbtBitPrefix.ByteCount(count)];
        for (int index = 0; index < count; index++)
        {
            if (prefix.GetBit(start + index) != 0) bytes[index >> 3] |= (byte)(1 << (7 - (index & 7)));
        }
        return new PbtBitPrefix(bytes, count);
    }

    private sealed class GroupOverlay(IPbtStore store, TrieUpdaterMetrics? metrics = null)
    {

        internal void SetLeaf(PbtWriteOperation operation) => store.SetLeaf(operation.Key, operation.Value);

        internal PbtNode Load(
            GroupFrame? activeGroup,
            PbtNodePath path,
            in ValueHash256 expectedHash,
            out GroupFrame group)
        {
            group = Resolve(activeGroup, path, out int position);
            try
            {
                PbtNode node = group.Load(position);
                if (node.Hash != expectedHash) throw new InvalidDataException("A persisted PBT node hash does not match its reference.");
                return node;
            }
            catch
            {
                if (!ReferenceEquals(group, activeGroup)) group.Dispose();
                throw;
            }
        }

        internal GroupFrame Resolve(GroupFrame? activeGroup, PbtNodePath path, out int position)
        {
            if (activeGroup is not null && activeGroup.TryGetPosition(path, out position)) return activeGroup;

            PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
            position = location.Position;
            metrics?.IncrementGroupCacheProbes();
            return new GroupFrame(store, location.GroupKey, metrics);
        }

        internal GroupFrame Store(GroupFrame? activeGroup, PbtNodePath path, PbtNode node)
        {
            GroupFrame group = Resolve(activeGroup, path, out int position);
            try
            {
                group.Store(position, node);
                return group;
            }
            catch
            {
                if (!ReferenceEquals(group, activeGroup)) group.Dispose();
                throw;
            }
        }

        internal void Remove(GroupFrame activeGroup, PbtNodePath path)
        {
            activeGroup.TryGetPosition(path, out int position);
            activeGroup.Remove(position);
        }
    }

    private sealed class GroupFrame(IPbtStore store, PbtNodePath groupKey, TrieUpdaterMetrics? metrics) : IDisposable
    {
        internal int BitDepth => groupKey.BitDepth;

        private readonly int[] _offsets = new int[PbtNodeGroupCodec.PositionCount];
        private readonly int[] _lengths = new int[PbtNodeGroupCodec.PositionCount];
        private readonly PbtNode?[] _nodes = new PbtNode[PbtNodeGroupCodec.PositionCount];
        private readonly SlotState[] _states = new SlotState[PbtNodeGroupCodec.PositionCount];
        private PbtNodeGroupPayload? _payload;
        private uint _knownPositions;
        private bool _fetched;

        internal bool TryGetPosition(PbtNodePath path, out int position)
        {
            if (PbtFourLevelGroupGeometry.GroupDepthOf(path.BitDepth) != groupKey.BitDepth
                || !StartsWith(path.Path, groupKey.Path, groupKey.BitDepth))
            {
                position = 0;
                return false;
            }

            position = PbtFourLevelGroupGeometry.PositionOf(path);
            return true;
        }

        internal PbtNode Load(int position)
        {
            EnsurePositionKnown(position);
            switch (_states[position])
            {
                case SlotState.Persisted:
                    return _nodes[position] ??= PbtNodeCodec.Decode(
                        _payload!.Span.Slice(_offsets[position], _lengths[position]));
                case SlotState.Staged:
                    return _nodes[position]!;
                default:
                    throw new InvalidDataException("A referenced PBT node is missing.");
            }
        }

        internal void Store(int position, PbtNode node)
        {
            byte[] encoding = PbtNodeCodec.Encode(node);
            if (CanSuppressWrite(position, encoding))
            {
                _states[position] = SlotState.Persisted;
                _nodes[position] = node;
                return;
            }

            _states[position] = SlotState.Staged;
            _nodes[position] = node;
            store.SetNode(PbtFourLevelGroupGeometry.PathOf(groupKey, position), encoding);
            metrics?.AddEmittedNodeWrites(1);
        }

        internal void Remove(int position)
        {
            EnsurePositionKnown(position);
            if (_lengths[position] != 0)
            {
                _states[position] = SlotState.Tombstone;
                store.SetNode(PbtFourLevelGroupGeometry.PathOf(groupKey, position), null);
                metrics?.AddEmittedNodeWrites(1);
            }
            else
            {
                _states[position] = SlotState.Absent;
            }
            _nodes[position] = null;
        }

        public void Dispose() => _payload?.Dispose();

        private void EnsurePositionKnown(int position)
        {
            if ((_knownPositions & (1u << position)) != 0) return;
            EnsureFetched();
        }

        private bool CanSuppressWrite(int position, ReadOnlySpan<byte> encoding)
        {
            if ((_knownPositions & (1u << position)) == 0) EnsureFetched();
            return _states[position] == SlotState.Persisted && PersistedEncodingEquals(position, encoding);
        }

        private void EnsureFetched()
        {
            if (_fetched) return;

            metrics?.IncrementPhysicalGroupFetches();
            PbtNodeGroupPayload? payload = store.GetNodeGroup(groupKey);
            if (payload is null)
            {
                _fetched = true;
                _knownPositions = uint.MaxValue;
                return;
            }

            try
            {
                metrics?.IncrementGroupParses();
                PbtNodeGroupReader reader = new(groupKey, payload.Span);
                for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
                {
                    if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
                    if (!reader.TryGetNodeRange(position, out int offset, out int length)) continue;

                    _offsets[position] = offset;
                    _lengths[position] = length;
                    if (_states[position] == SlotState.Absent) _states[position] = SlotState.Persisted;
                }
                _payload = payload;
                _fetched = true;
                _knownPositions = uint.MaxValue;
            }
            catch
            {
                payload.Dispose();
                throw;
            }
        }

        private bool PersistedEncodingEquals(int position, ReadOnlySpan<byte> encoding) =>
            _lengths[position] != 0
            && _payload!.Span.Slice(_offsets[position], _lengths[position]).SequenceEqual(encoding);

        private static bool StartsWith(ReadOnlySpan<byte> path, ReadOnlySpan<byte> prefix, int prefixBitCount)
        {
            int completeBytes = prefixBitCount >> 3;
            if (!path[..completeBytes].SequenceEqual(prefix[..completeBytes])) return false;
            int remainingBits = prefixBitCount & 7;
            return remainingBits == 0
                || ((path[completeBytes] ^ prefix[completeBytes]) & (0xFF << (8 - remainingBits))) == 0;
        }

        private enum SlotState : byte
        {
            Absent,
            Persisted,
            Staged,
            Tombstone,
        }
    }
}

internal sealed class TrieUpdaterMetrics
{
    internal int PhysicalGroupFetches { get; private set; }
    internal int GroupParses { get; private set; }
    internal int GroupCacheProbes { get; private set; }
    internal int EmittedNodeWrites { get; private set; }

    internal void IncrementPhysicalGroupFetches() => PhysicalGroupFetches++;
    internal void IncrementGroupParses() => GroupParses++;
    internal void IncrementGroupCacheProbes() => GroupCacheProbes++;
    internal void AddEmittedNodeWrites(int count) => EmittedNodeWrites += count;
}
