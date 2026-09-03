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

        using GroupFrameCache cache = new(store, metrics);
        GroupFrame rootGroup = cache.Resolve(RootPath, out _);
        if (operations.Length > 1)
            BucketizeByGroupBoundary(operations, rootGroup.BitDepth);
        return FoldMutationsInGroup(store, metrics, rootGroup, RootPath, operations.AsSpan(), allowAbsent: true);
    }

    private static ValueHash256 FoldMutations(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrame? activeGroup,
        PbtNodePath path,
        Span<PbtWriteOperation> operations)
    {
        if (activeGroup is not null && activeGroup.TryGetPosition(path, out _))
            return FoldMutationsInGroup(store, metrics, activeGroup, path, operations);

        GroupFrame group = Resolve(store, metrics, activeGroup, path, out _);
        bool ownsGroup = OwnsGroup(group, activeGroup);
        try
        {
            if (operations.Length > 1)
                BucketizeByGroupBoundary(operations, group.BitDepth);
            return FoldMutationsInGroup(store, metrics, group, path, operations);
        }
        finally
        {
            if (ownsGroup) group.Dispose();
        }
    }

    private static ValueHash256 FoldMutationsInGroup(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrame group,
        PbtNodePath path,
        Span<PbtWriteOperation> operations,
        bool allowAbsent = false)
    {
        PbtNode? currentNode = allowAbsent ? group.TryLoad(path) : group.Load(path);
        if (currentNode is null)
        {
            int setCount = RetainSets(operations);
            return setCount == 0 ? default : BuildSubtreeInGroup(store, metrics, group, path, operations[..setCount]);
        }

        PbtNode current = currentNode;
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
                Remove(group, path);
                return setCount == 0 ? default : BuildSubtree(store, metrics, group, path, operations[..setCount]);
            }
            if (setCount == 0)
            {
                if (surviving.Hash != leaf.Hash) Store(store, metrics, group, path, surviving);
                return surviving.Hash;
            }
            return InsertSetsIntoNode(store, metrics, group, path, surviving, operations[..setCount]);
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
            leftHash = FoldMutations(store, metrics, group, leftPath, matchingOperations[..partition]);
        if (partition < matchingOperations.Length)
            rightHash = FoldMutations(store, metrics, group, rightPath, matchingOperations[partition..]);

        ValueHash256 reconciledHash = CanonicalizeBranch(store, metrics, group, path, branch.Prefix, leftPath, leftHash, rightPath, rightHash);
        int divergentSetCount = RetainSets(operations[matchingCount..]);
        Span<PbtWriteOperation> divergentSets = operations.Slice(matchingCount, divergentSetCount);
        if (divergentSets.Length == 0) return reconciledHash;
        if (reconciledHash == default)
            return BuildSubtree(store, metrics, group, path, divergentSets);
        GroupFrame reconciledGroup = Resolve(store, metrics, group, path, out _);
        bool ownsReconciledGroup = OwnsGroup(reconciledGroup, group);
        PbtNode reconciled;
        try
        {
            reconciled = reconciledGroup.Load(path);
        }
        catch
        {
            if (ownsReconciledGroup) reconciledGroup.Dispose();
            throw;
        }
        try
        {
            return InsertSetsIntoNode(store, metrics, reconciledGroup, path, reconciled, divergentSets);
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
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
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
            Store(store, metrics, group, path, replacement);
            return replacement.Hash;
        }
        if (leftHash == default && rightHash == default)
        {
            Remove(group, path);
            return default;
        }

        int remainingDirection = leftHash != default ? 0 : 1;
        PbtNodePath remainingPath = remainingDirection == 0 ? leftPath : rightPath;
        GroupFrame remainingGroup = Resolve(store, metrics, group, remainingPath, out _);
        bool ownsRemainingGroup = OwnsGroup(remainingGroup, group);
        PbtNode remaining;
        try
        {
            remaining = remainingGroup.Load(remainingPath);
        }
        catch
        {
            if (ownsRemainingGroup) remainingGroup.Dispose();
            throw;
        }
        try
        {
            Remove(remainingGroup, remainingPath);
            PbtNode promoted = remaining is PbtBranchNode remainingBranch
                ? new PbtBranchNode(PbtBitPrefix.Concat(prefix, remainingDirection, remainingBranch.Prefix), remainingBranch.LeftHash, remainingBranch.RightHash)
                : remaining;
            Store(store, metrics, group, path, promoted);
            return promoted.Hash;
        }
        finally
        {
            if (ownsRemainingGroup) remainingGroup.Dispose();
        }
    }

    private static ValueHash256 InsertSets(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrame? activeGroup,
        PbtNodePath path,
        Span<PbtWriteOperation> sets)
    {
        if (activeGroup is not null && activeGroup.TryGetPosition(path, out _))
            return InsertSetsInGroup(store, metrics, activeGroup, path, sets);

        GroupFrame group = Resolve(store, metrics, activeGroup, path, out _);
        bool ownsGroup = OwnsGroup(group, activeGroup);
        try
        {
            if (sets.Length > 1)
                BucketizeByGroupBoundary(sets, group.BitDepth);
            return InsertSetsInGroup(store, metrics, group, path, sets);
        }
        finally
        {
            if (ownsGroup) group.Dispose();
        }
    }

    private static ValueHash256 InsertSetsInGroup(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrame group,
        PbtNodePath path,
        Span<PbtWriteOperation> sets)
    {
        PbtNode current = group.Load(path);
        return InsertSetsIntoNode(store, metrics, group, path, current, sets);
    }

    private static ValueHash256 InsertSetsIntoNode(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrame? activeGroup,
        PbtNodePath path,
        PbtNode current,
        Span<PbtWriteOperation> sets) => current switch
        {
            PbtLeafNode leaf => InsertSetsIntoLeaf(store, metrics, activeGroup, path, leaf, sets),
            PbtBranchNode branch => InsertSetsIntoBranch(store, metrics, activeGroup, path, branch, sets),
            _ => throw new ArgumentOutOfRangeException(nameof(current)),
        };

    private static ValueHash256 InsertSetsIntoLeaf(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrame? activeGroup,
        PbtNodePath path,
        PbtLeafNode leaf,
        Span<PbtWriteOperation> sets)
    {
        GroupFrame group = Resolve(store, metrics, activeGroup, path, out _);
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
            store.SetLeaf(sets[0].Key, sets[0].Value);
            PbtLeafNode replacement = new(sets[0].Key, sets[0].Value.Bytes.ToArray());
            Store(store, metrics, group, path, replacement);
            return replacement.Hash;
        }

        PbtBitPrefix common = PbtBitPrefix.FromKey(leaf.Key, path.BitDepth, differingBit - path.BitDepth);
        int existingDirection = leaf.Key.GetBit(differingBit);
        int partition = PartitionByBit(sets, differingBit);
        PbtNodePath leftPath = path.Append(common, 0);
        PbtNodePath rightPath = path.Append(common, 1);
        PbtNodePath existingPath = existingDirection == 0 ? leftPath : rightPath;
        GroupFrame existingGroup = Store(store, metrics, group, existingPath, leaf);
        bool ownsExistingGroup = OwnsGroup(existingGroup, group);
        try
        {
            ValueHash256 leftHash;
            ValueHash256 rightHash;
            if (existingDirection == 0)
            {
                leftHash = partition > 0
                    ? InsertSetsIntoNode(store, metrics, existingGroup, leftPath, leaf, sets[..partition])
                    : leaf.Hash;
                rightHash = BuildSubtree(store, metrics, group, rightPath, sets[partition..]);
            }
            else
            {
                leftHash = BuildSubtree(store, metrics, group, leftPath, sets[..partition]);
                rightHash = partition < sets.Length
                    ? InsertSetsIntoNode(store, metrics, existingGroup, rightPath, leaf, sets[partition..])
                    : leaf.Hash;
            }

            PbtBranchNode split = new(common, leftHash, rightHash);
            Store(store, metrics, group, path, split);
            return split.Hash;
        }
        finally
        {
            if (ownsExistingGroup) existingGroup.Dispose();
        }
    }

    private static ValueHash256 InsertSetsIntoBranch(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrame? activeGroup,
        PbtNodePath path,
        PbtBranchNode branch,
        Span<PbtWriteOperation> sets)
    {
        GroupFrame group = Resolve(store, metrics, activeGroup, path, out _);
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
            GroupFrame existingGroup = Store(store, metrics, group, existingPath, relocated);
            bool ownsExistingGroup = OwnsGroup(existingGroup, group);
            try
            {
                ValueHash256 leftHash;
                ValueHash256 rightHash;
                if (existingDirection == 0)
                {
                    leftHash = partition > 0
                        ? InsertSetsIntoNode(store, metrics, existingGroup, leftPath, relocated, sets[..partition])
                        : relocated.Hash;
                    rightHash = BuildSubtree(store, metrics, group, rightPath, sets[partition..]);
                }
                else
                {
                    leftHash = BuildSubtree(store, metrics, group, leftPath, sets[..partition]);
                    rightHash = partition < sets.Length
                        ? InsertSetsIntoNode(store, metrics, existingGroup, rightPath, relocated, sets[partition..])
                        : relocated.Hash;
                }

                PbtBranchNode split = new(common, leftHash, rightHash);
                Store(store, metrics, group, path, split);
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
            replacementLeft = InsertSets(store, metrics, group, leftPath, sets[..childPartition]);
        }
        if (childPartition < sets.Length)
        {
            PbtNodePath rightPath = path.Append(branch.Prefix, 1);
            replacementRight = InsertSets(store, metrics, group, rightPath, sets[childPartition..]);
        }

        PbtBranchNode replacement = new(branch.Prefix, replacementLeft, replacementRight);
        Store(store, metrics, group, path, replacement);
        return replacement.Hash;
    }

    private static ValueHash256 BuildSubtree(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrame? activeGroup,
        PbtNodePath path,
        Span<PbtWriteOperation> sets)
    {
        if (activeGroup is not null && activeGroup.TryGetPosition(path, out _))
            return BuildSubtreeInGroup(store, metrics, activeGroup, path, sets);

        GroupFrame group = Resolve(store, metrics, activeGroup, path, out _);
        bool ownsGroup = OwnsGroup(group, activeGroup);
        try
        {
            if (sets.Length > 1)
                BucketizeByGroupBoundary(sets, group.BitDepth);
            return BuildSubtreeInGroup(store, metrics, group, path, sets);
        }
        finally
        {
            if (ownsGroup) group.Dispose();
        }
    }

    private static ValueHash256 BuildSubtreeInGroup(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrame group,
        PbtNodePath path,
        Span<PbtWriteOperation> sets)
    {
        if (sets.Length == 1)
        {
            PbtWriteOperation operation = sets[0];
            store.SetLeaf(operation.Key, operation.Value);
            PbtLeafNode leaf = new(operation.Key, operation.Value.Bytes.ToArray());
            Store(store, metrics, group, path, leaf);
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
        ValueHash256 leftHash = BuildSubtree(store, metrics, group, leftPath, sets[..partition]);
        ValueHash256 rightHash = BuildSubtree(store, metrics, group, rightPath, sets[partition..]);
        PbtBranchNode branch = new(prefix, leftHash, rightHash);
        Store(store, metrics, group, path, branch);
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

    private static GroupFrame Resolve(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrame? activeGroup,
        PbtNodePath path,
        out int position)
    {
        if (activeGroup is not null)
        {
            if (activeGroup.TryGetPosition(path, out position)) return activeGroup;
            return activeGroup.Cache is GroupFrameCache cache
                ? cache.Resolve(path, out position)
                : Resolve(store, metrics, null, path, out position);
        }

        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
        position = location.Position;
        metrics?.IncrementGroupCacheProbes();
        return new GroupFrame(store, location.GroupKey, metrics);
    }

    private static bool OwnsGroup(GroupFrame group, GroupFrame? activeGroup) =>
        !ReferenceEquals(group, activeGroup) && group.Cache is null;

    private static GroupFrame Store(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrame? activeGroup,
        PbtNodePath path,
        PbtNode node)
    {
        GroupFrame group = Resolve(store, metrics, activeGroup, path, out int position);
        try
        {
            group.Store(position, node);
            return group;
        }
        catch
        {
            if (OwnsGroup(group, activeGroup)) group.Dispose();
            throw;
        }
    }

    private static void Remove(GroupFrame activeGroup, PbtNodePath path)
    {
        activeGroup.TryGetPosition(path, out int position);
        activeGroup.Remove(position);
    }

    private sealed class GroupFrameCache(IPbtStore store, TrieUpdaterMetrics? metrics) : IDisposable
    {
        private readonly Dictionary<PbtNodePath, GroupFrame> _frames = [];

        internal GroupFrame Resolve(PbtNodePath path, out int position)
        {
            PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
            position = location.Position;
            metrics?.IncrementGroupCacheProbes();
            if (_frames.TryGetValue(location.GroupKey, out GroupFrame? frame)) return frame;
            frame = new GroupFrame(store, location.GroupKey, metrics, this);
            _frames.Add(location.GroupKey, frame);
            return frame;
        }

        public void Dispose()
        {
            foreach (GroupFrame frame in _frames.Values) frame.Dispose();
            _frames.Clear();
        }
    }

    private sealed class GroupFrame(IPbtStore store, PbtNodePath groupKey, TrieUpdaterMetrics? metrics, GroupFrameCache? cache = null) : IDisposable
    {
        internal int BitDepth => groupKey.BitDepth;
        internal GroupFrameCache? Cache => cache;

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

        internal PbtNode? TryLoad(PbtNodePath path)
        {
            TryGetPosition(path, out int position);
            EnsurePositionKnown(position);
            return _states[position] switch
            {
                SlotState.Absent => null,
                SlotState.Persisted => _nodes[position] ??= PbtNodeCodec.Decode(
                    _payload!.Span.Slice(_offsets[position], _lengths[position])),
                SlotState.Staged => _nodes[position],
                SlotState.Tombstone => throw new InvalidDataException("A referenced PBT node is missing."),
                _ => throw new ArgumentOutOfRangeException(nameof(path)),
            };
        }

        internal PbtNode Load(PbtNodePath path)
        {
            TryGetPosition(path, out int position);
            EnsurePositionKnown(position);
            return _states[position] switch
            {
                SlotState.Persisted => _nodes[position] ??= PbtNodeCodec.Decode(
                    _payload!.Span.Slice(_offsets[position], _lengths[position])),
                SlotState.Staged => _nodes[position]!,
                _ => throw new InvalidDataException("A referenced PBT node is missing."),
            };
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
