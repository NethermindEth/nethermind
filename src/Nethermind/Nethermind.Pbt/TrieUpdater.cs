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
    /// The final complete-key set is validated before any node is changed. Deletes and sets are each
    /// folded serially through the tree as a sorted bulk range, so mutations sharing a path share its
    /// traversal. All leaf and node writes are staged and handed to <see cref="IPbtStore.Apply"/> only
    /// after the complete mutation succeeds.
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

        SortedDictionary<PbtFullKey, PbtWriteOperation> operations = [];
        foreach (PbtWriteOperation operation in changes.Operations) operations[operation.Key] = operation;

        int deleteCount = 0;
        foreach (PbtWriteOperation operation in operations.Values)
        {
            if (operation.Kind == PbtWriteOperationKind.Delete) deleteCount++;
        }

        PbtWriteOperation[] deletes = new PbtWriteOperation[deleteCount];
        PbtWriteOperation[] sets = new PbtWriteOperation[operations.Count - deleteCount];
        int deleteIndex = 0;
        int setIndex = 0;
        foreach (PbtWriteOperation operation in operations.Values)
        {
            if (operation.Kind == PbtWriteOperationKind.Delete) deletes[deleteIndex++] = operation;
            else sets[setIndex++] = operation;
        }

        ValidateBatchKeys(sets);
        GroupOverlay overlay = new(store, metrics);
        try
        {
            ValueHash256 root = currentRoot;
            if (root != default && deletes.Length != 0)
                root = FoldDeletes(overlay, null, RootPath, root, deletes, 0, deletes.Length, out _);
            if (sets.Length != 0)
            {
                root = FoldSets(overlay, null, RootPath, root, sets, 0, sets.Length);
                foreach (PbtWriteOperation operation in sets) overlay.SetLeaf(operation.Key, operation.Value);
            }

            IReadOnlyList<PbtLeafMutation> leafMutations = overlay.LeafMutations;
            IReadOnlyList<PbtNodeMutation> nodeMutations = overlay.NodeMutations;
            store.Apply(root, leafMutations, nodeMutations);
            return root;
        }
        finally
        {
            overlay.Dispose();
        }
    }

    private static ValueHash256 FoldDeletes(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        in ValueHash256 expectedHash,
        PbtWriteOperation[] deletes,
        int start,
        int end,
        out bool changed)
    {
        PbtNode current = overlay.Load(activeGroup, path, expectedHash, out GroupFrame group);
        if (current is PbtLeafNode leaf)
        {
            for (int index = start; index < end; index++)
            {
                int comparison = deletes[index].Key.CompareTo(leaf.Key);
                if (comparison < 0) continue;
                if (comparison > 0) break;

                overlay.Remove(group, path);
                overlay.SetLeaf(leaf.Key, null);
                changed = true;
                return default;
            }

            changed = false;
            return leaf.Hash;
        }

        PbtBranchNode branch = (PbtBranchNode)current;
        FindMatchingBranchRange(branch, path.BitDepth, deletes, start, end, out int matchingStart, out int matchingEnd);
        if (matchingStart == matchingEnd)
        {
            changed = false;
            return branch.Hash;
        }

        int directionBit = path.BitDepth + branch.Prefix.BitCount;
        int partition = PartitionByBit(deletes, matchingStart, matchingEnd, directionBit);
        PbtNodePath leftPath = path.Append(branch.Prefix, 0);
        PbtNodePath rightPath = path.Append(branch.Prefix, 1);
        ValueHash256 leftHash = branch.LeftHash;
        ValueHash256 rightHash = branch.RightHash;
        bool leftChanged = false;
        bool rightChanged = false;
        if (matchingStart < partition)
            leftHash = FoldDeletes(overlay, group, leftPath, leftHash, deletes, matchingStart, partition, out leftChanged);
        if (partition < matchingEnd)
            rightHash = FoldDeletes(overlay, group, rightPath, rightHash, deletes, partition, matchingEnd, out rightChanged);

        changed = leftChanged || rightChanged;
        if (!changed) return branch.Hash;
        if (leftHash != default && rightHash != default)
        {
            PbtBranchNode replacement = new(branch.Prefix, leftHash, rightHash);
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
        overlay.Remove(remainingGroup, remainingPath);
        PbtNode promoted = remaining is PbtBranchNode remainingBranch
            ? new PbtBranchNode(
                PbtBitPrefix.Concat(branch.Prefix, remainingDirection, remainingBranch.Prefix),
                remainingBranch.LeftHash,
                remainingBranch.RightHash)
            : remaining;
        overlay.Store(group, path, promoted);
        return promoted.Hash;
    }

    private static ValueHash256 FoldSets(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        in ValueHash256 expectedHash,
        PbtWriteOperation[] sets,
        int start,
        int end)
    {
        if (expectedHash == default) return BuildSubtree(overlay, activeGroup, path, sets, start, end);
        PbtNode current = overlay.Load(activeGroup, path, expectedHash, out GroupFrame group);
        return FoldSetsIntoNode(overlay, group, path, current, sets, start, end);
    }

    private static ValueHash256 FoldSetsIntoNode(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        PbtNode current,
        PbtWriteOperation[] sets,
        int start,
        int end) => current switch
        {
            PbtLeafNode leaf => FoldSetsIntoLeaf(overlay, activeGroup, path, leaf, sets, start, end),
            PbtBranchNode branch => FoldSetsIntoBranch(overlay, activeGroup, path, branch, sets, start, end),
            _ => throw new ArgumentOutOfRangeException(nameof(current)),
        };

    private static ValueHash256 FoldSetsIntoLeaf(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        PbtLeafNode leaf,
        PbtWriteOperation[] sets,
        int start,
        int end)
    {
        GroupFrame group = overlay.Resolve(activeGroup, path, out _);
        int differingBit = int.MaxValue;
        for (int index = start; index < end; index++)
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
            PbtLeafNode replacement = new(sets[start].Key, sets[start].Value.Bytes.ToArray());
            overlay.Store(group, path, replacement);
            return replacement.Hash;
        }

        PbtBitPrefix common = PbtBitPrefix.FromKey(leaf.Key, path.BitDepth, differingBit - path.BitDepth);
        int existingDirection = leaf.Key.GetBit(differingBit);
        int partition = PartitionByBit(sets, start, end, differingBit);
        PbtNodePath leftPath = path.Append(common, 0);
        PbtNodePath rightPath = path.Append(common, 1);
        PbtNodePath existingPath = existingDirection == 0 ? leftPath : rightPath;
        GroupFrame existingGroup = overlay.Store(group, existingPath, leaf);

        ValueHash256 leftHash;
        ValueHash256 rightHash;
        if (existingDirection == 0)
        {
            leftHash = start < partition
                ? FoldSetsIntoNode(overlay, existingGroup, leftPath, leaf, sets, start, partition)
                : leaf.Hash;
            rightHash = BuildSubtree(overlay, group, rightPath, sets, partition, end);
        }
        else
        {
            leftHash = BuildSubtree(overlay, group, leftPath, sets, start, partition);
            rightHash = partition < end
                ? FoldSetsIntoNode(overlay, existingGroup, rightPath, leaf, sets, partition, end)
                : leaf.Hash;
        }

        PbtBranchNode split = new(common, leftHash, rightHash);
        overlay.Store(group, path, split);
        return split.Hash;
    }

    private static ValueHash256 FoldSetsIntoBranch(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        PbtBranchNode branch,
        PbtWriteOperation[] sets,
        int start,
        int end)
    {
        GroupFrame group = overlay.Resolve(activeGroup, path, out _);
        int firstMismatch = branch.Prefix.BitCount;
        for (int index = start; index < end; index++)
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
            int partition = PartitionByBit(sets, start, end, directionBit);
            PbtNodePath leftPath = path.Append(common, 0);
            PbtNodePath rightPath = path.Append(common, 1);
            PbtNodePath existingPath = existingDirection == 0 ? leftPath : rightPath;
            GroupFrame existingGroup = overlay.Store(group, existingPath, relocated);

            ValueHash256 leftHash;
            ValueHash256 rightHash;
            if (existingDirection == 0)
            {
                leftHash = start < partition
                    ? FoldSetsIntoNode(overlay, existingGroup, leftPath, relocated, sets, start, partition)
                    : relocated.Hash;
                rightHash = BuildSubtree(overlay, group, rightPath, sets, partition, end);
            }
            else
            {
                leftHash = BuildSubtree(overlay, group, leftPath, sets, start, partition);
                rightHash = partition < end
                    ? FoldSetsIntoNode(overlay, existingGroup, rightPath, relocated, sets, partition, end)
                    : relocated.Hash;
            }

            PbtBranchNode split = new(common, leftHash, rightHash);
            overlay.Store(group, path, split);
            return split.Hash;
        }

        int childDirectionBit = path.BitDepth + branch.Prefix.BitCount;
        int childPartition = PartitionByBit(sets, start, end, childDirectionBit);
        ValueHash256 replacementLeft = branch.LeftHash;
        ValueHash256 replacementRight = branch.RightHash;
        if (start < childPartition)
        {
            PbtNodePath leftPath = path.Append(branch.Prefix, 0);
            replacementLeft = FoldSets(overlay, group, leftPath, replacementLeft, sets, start, childPartition);
        }
        if (childPartition < end)
        {
            PbtNodePath rightPath = path.Append(branch.Prefix, 1);
            replacementRight = FoldSets(overlay, group, rightPath, replacementRight, sets, childPartition, end);
        }

        PbtBranchNode replacement = new(branch.Prefix, replacementLeft, replacementRight);
        overlay.Store(group, path, replacement);
        return replacement.Hash;
    }

    private static ValueHash256 BuildSubtree(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        PbtWriteOperation[] sets,
        int start,
        int end)
    {
        GroupFrame group = overlay.Resolve(activeGroup, path, out _);
        if (end - start == 1)
        {
            PbtWriteOperation operation = sets[start];
            PbtLeafNode leaf = new(operation.Key, operation.Value.Bytes.ToArray());
            overlay.Store(group, path, leaf);
            return leaf.Hash;
        }

        PbtFullKey firstKey = sets[start].Key;
        PbtFullKey lastKey = sets[end - 1].Key;
        int differingBit = firstKey.FirstDifferingBit(lastKey, path.BitDepth);
        if (differingBit == Math.Min(firstKey.BitLength, lastKey.BitLength))
            throw new ArgumentException("Tree keys must be prefix-free.", nameof(sets));
        PbtBitPrefix prefix = PbtBitPrefix.FromKey(firstKey, path.BitDepth, differingBit - path.BitDepth);
        int partition = PartitionByBit(sets, start, end, differingBit);
        PbtNodePath leftPath = path.Append(prefix, 0);
        PbtNodePath rightPath = path.Append(prefix, 1);
        ValueHash256 leftHash = BuildSubtree(overlay, group, leftPath, sets, start, partition);
        ValueHash256 rightHash = BuildSubtree(overlay, group, rightPath, sets, partition, end);
        PbtBranchNode branch = new(prefix, leftHash, rightHash);
        overlay.Store(group, path, branch);
        return branch.Hash;
    }

    private static void FindMatchingBranchRange(
        PbtBranchNode branch,
        int bitDepth,
        PbtWriteOperation[] operations,
        int start,
        int end,
        out int matchingStart,
        out int matchingEnd)
    {
        matchingStart = end;
        matchingEnd = end;
        for (int index = start; index < end; index++)
        {
            PbtFullKey key = operations[index].Key;
            int available = key.BitLength - bitDepth;
            bool matches = available > branch.Prefix.BitCount &&
                MatchingPrefixBits(branch.Prefix, key, bitDepth) == branch.Prefix.BitCount;
            if (!matches)
            {
                if (matchingStart != end) break;
                continue;
            }

            if (matchingStart == end) matchingStart = index;
            matchingEnd = index + 1;
        }
    }

    private static int PartitionByBit(PbtWriteOperation[] operations, int start, int end, int bitIndex)
    {
        int low = start;
        int high = end;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (operations[middle].Key.GetBit(bitIndex) == 0) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static void ValidateBatchKeys(PbtWriteOperation[] sets)
    {
        for (int index = 1; index < sets.Length; index++)
        {
            if (sets[index - 1].Key.IsPrefixOf(sets[index].Key))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(sets));
        }
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

    private sealed class GroupOverlay(IPbtStore store, TrieUpdaterMetrics? metrics = null) : IDisposable
    {
        private readonly Dictionary<PbtFullKey, ValueHash256?> _leaves = [];
        private readonly Dictionary<PbtNodePath, GroupFrame> _groups = [];

        internal IReadOnlyList<PbtLeafMutation> LeafMutations
        {
            get
            {
                List<PbtLeafMutation> mutations = new(_leaves.Count);
                foreach ((PbtFullKey key, ValueHash256? value) in _leaves) mutations.Add(new(key, value));
                return mutations;
            }
        }

        internal IReadOnlyList<PbtNodeMutation> NodeMutations
        {
            get
            {
                List<PbtNodeMutation> mutations = [];
                foreach (GroupFrame group in _groups.Values) group.AddMutations(mutations);
                metrics?.AddEmittedNodeWrites(mutations.Count);
                return mutations;
            }
        }

        internal void SetLeaf(PbtFullKey key, ValueHash256? value) => _leaves[key] = value;

        internal PbtNode Load(
            GroupFrame? activeGroup,
            PbtNodePath path,
            in ValueHash256 expectedHash,
            out GroupFrame group)
        {
            group = Resolve(activeGroup, path, out int position);
            PbtNode node = group.Load(position);
            if (node.Hash != expectedHash) throw new InvalidDataException("A persisted PBT node hash does not match its reference.");
            return node;
        }

        internal GroupFrame Resolve(GroupFrame? activeGroup, PbtNodePath path, out int position)
        {
            if (activeGroup is not null && activeGroup.TryGetPosition(path, out position)) return activeGroup;

            PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
            position = location.Position;
            metrics?.IncrementGroupCacheProbes();
            if (_groups.TryGetValue(location.GroupKey, out GroupFrame? group)) return group;

            group = new GroupFrame(store, location.GroupKey, metrics);
            _groups.Add(location.GroupKey, group);
            return group;
        }

        internal GroupFrame Store(GroupFrame? activeGroup, PbtNodePath path, PbtNode node)
        {
            GroupFrame group = Resolve(activeGroup, path, out int position);
            group.Store(position, node);
            return group;
        }

        internal void Remove(GroupFrame? activeGroup, PbtNodePath path)
        {
            GroupFrame group = Resolve(activeGroup, path, out int position);
            group.Remove(position);
        }

        public void Dispose()
        {
            foreach (GroupFrame group in _groups.Values) group.Dispose();
            _groups.Clear();
        }
    }

    private sealed class GroupFrame(IPbtStore store, PbtNodePath groupKey, TrieUpdaterMetrics? metrics) : IDisposable
    {
        private readonly int[] _offsets = new int[PbtNodeGroupCodec.PositionCount];
        private readonly int[] _lengths = new int[PbtNodeGroupCodec.PositionCount];
        private readonly PbtNode?[] _nodes = new PbtNode[PbtNodeGroupCodec.PositionCount];
        private readonly byte[]?[] _stagedEncodings = new byte[PbtNodeGroupCodec.PositionCount][];
        private readonly SlotState[] _states = new SlotState[PbtNodeGroupCodec.PositionCount];
        private PbtNodeGroupPayload? _payload;
        private uint _dirtyPositions;
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
                _stagedEncodings[position] = null;
                _nodes[position] = node;
                _dirtyPositions &= ~(1u << position);
                return;
            }

            _states[position] = SlotState.Staged;
            _stagedEncodings[position] = encoding;
            _nodes[position] = node;
            _dirtyPositions |= 1u << position;
        }

        internal void Remove(int position)
        {
            EnsurePositionKnown(position);
            if (_lengths[position] != 0)
            {
                _states[position] = SlotState.Tombstone;
                _dirtyPositions |= 1u << position;
            }
            else
            {
                _states[position] = SlotState.Absent;
                _dirtyPositions &= ~(1u << position);
            }
            _stagedEncodings[position] = null;
            _nodes[position] = null;
        }

        internal void AddMutations(List<PbtNodeMutation> mutations)
        {
            uint remaining = _dirtyPositions;
            for (int position = 0; remaining != 0; position++, remaining >>= 1)
            {
                if ((remaining & 1) == 0) continue;
                byte[]? encoding = _states[position] == SlotState.Staged ? _stagedEncodings[position] : null;
                mutations.Add(new(PbtFourLevelGroupGeometry.PathOf(groupKey, position), encoding));
            }
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
            return PersistedEncodingEquals(position, encoding);
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
