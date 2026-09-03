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
    /// The final complete-key set is validated before any node is changed. Effective mutations are
    /// folded through the tree as traversal-local partitioned ranges, so mutations sharing a path share
    /// one traversal. All leaf and node writes are staged and handed to <see cref="IPbtStore.Apply"/> only
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

        Dictionary<PbtFullKey, PbtWriteOperation> operations = [];
        foreach (PbtWriteOperation operation in changes.Operations) operations[operation.Key] = operation;

        int deleteCount = 0;
        foreach (PbtWriteOperation operation in operations.Values)
        {
            if (operation.Kind == PbtWriteOperationKind.Delete) deleteCount++;
        }

        PbtWriteOperation[] effectiveOperations = new PbtWriteOperation[operations.Count];
        int deleteIndex = 0;
        int setIndex = deleteCount;
        foreach (PbtWriteOperation operation in operations.Values)
        {
            if (operation.Kind == PbtWriteOperationKind.Delete) effectiveOperations[deleteIndex++] = operation;
            else effectiveOperations[setIndex++] = operation;
        }

        ValidateBatchKeys(effectiveOperations, deleteCount, effectiveOperations.Length, 0);
        GroupOverlay overlay = new(store, metrics);
        try
        {
            for (int index = deleteCount; index < effectiveOperations.Length; index++)
            {
                PbtWriteOperation operation = effectiveOperations[index];
                overlay.SetLeaf(operation.Key, operation.Value);
            }

            ValueHash256 root = FoldMutations(overlay, null, RootPath, currentRoot, effectiveOperations, 0, effectiveOperations.Length);
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

    private static ValueHash256 FoldMutations(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        in ValueHash256 expectedHash,
        PbtWriteOperation[] operations,
        int start,
        int end)
    {
        if (expectedHash == default)
        {
            int setEnd = RetainSets(operations, start, end);
            return start == setEnd ? default : BuildSubtree(overlay, activeGroup, path, operations, start, setEnd);
        }

        PbtNode current = overlay.Load(activeGroup, path, expectedHash, out GroupFrame group);
        if (current is PbtLeafNode leaf)
        {
            PbtNode? surviving = leaf;
            for (int index = start; index < end; index++)
            {
                PbtWriteOperation operation = operations[index];
                if (!operation.Key.Equals(leaf.Key)) continue;
                if (operation.Kind == PbtWriteOperationKind.Delete)
                {
                    overlay.SetLeaf(leaf.Key, null);
                    surviving = null;
                }
                else
                {
                    surviving = new PbtLeafNode(operation.Key, operation.Value.Bytes.ToArray());
                }
                break;
            }

            int setEnd = start;
            for (int index = start; index < end; index++)
            {
                PbtWriteOperation operation = operations[index];
                if (operation.Kind == PbtWriteOperationKind.Delete || operation.Key.Equals(leaf.Key)) continue;
                (operations[setEnd], operations[index]) = (operations[index], operations[setEnd]);
                setEnd++;
            }

            if (surviving is null)
            {
                overlay.Remove(group, path);
                return start == setEnd ? default : BuildSubtree(overlay, group, path, operations, start, setEnd);
            }
            if (start == setEnd)
            {
                if (surviving.Hash != leaf.Hash) overlay.Store(group, path, surviving);
                return surviving.Hash;
            }
            return InsertSetsIntoNode(overlay, group, path, surviving, operations, start, setEnd);
        }

        PbtBranchNode branch = (PbtBranchNode)current;
        FindMatchingBranchRange(branch, path.BitDepth, operations, start, end, out int matchingStart, out int matchingEnd);
        int directionBit = path.BitDepth + branch.Prefix.BitCount;
        int partition = PartitionByBit(operations, matchingStart, matchingEnd, directionBit);
        PbtNodePath leftPath = path.Append(branch.Prefix, 0);
        PbtNodePath rightPath = path.Append(branch.Prefix, 1);
        ValueHash256 leftHash = branch.LeftHash;
        ValueHash256 rightHash = branch.RightHash;
        if (matchingStart < partition)
            leftHash = FoldMutations(overlay, group, leftPath, leftHash, operations, matchingStart, partition);
        if (partition < matchingEnd)
            rightHash = FoldMutations(overlay, group, rightPath, rightHash, operations, partition, matchingEnd);

        ValueHash256 reconciledHash = CanonicalizeBranch(overlay, group, path, branch.Prefix, leftPath, leftHash, rightPath, rightHash);
        int divergentSetEnd = RetainSets(operations, matchingEnd, end);
        if (matchingEnd == divergentSetEnd) return reconciledHash;
        if (reconciledHash == default)
            return BuildSubtree(overlay, group, path, operations, matchingEnd, divergentSetEnd);
        PbtNode reconciled = overlay.Load(group, path, reconciledHash, out GroupFrame reconciledGroup);
        return InsertSetsIntoNode(overlay, reconciledGroup, path, reconciled, operations, matchingEnd, divergentSetEnd);
    }

    private static int RetainSets(PbtWriteOperation[] operations, int start, int end)
    {
        int setEnd = start;
        for (int index = start; index < end; index++)
        {
            if (operations[index].Kind == PbtWriteOperationKind.Delete) continue;
            (operations[setEnd], operations[index]) = (operations[index], operations[setEnd]);
            setEnd++;
        }
        return setEnd;
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
        overlay.Remove(remainingGroup, remainingPath);
        PbtNode promoted = remaining is PbtBranchNode remainingBranch
            ? new PbtBranchNode(PbtBitPrefix.Concat(prefix, remainingDirection, remainingBranch.Prefix), remainingBranch.LeftHash, remainingBranch.RightHash)
            : remaining;
        overlay.Store(group, path, promoted);
        return promoted.Hash;
    }

    private static ValueHash256 InsertSets(
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
        return InsertSetsIntoNode(overlay, group, path, current, sets, start, end);
    }

    private static ValueHash256 InsertSetsIntoNode(
        GroupOverlay overlay,
        GroupFrame? activeGroup,
        PbtNodePath path,
        PbtNode current,
        PbtWriteOperation[] sets,
        int start,
        int end) => current switch
        {
            PbtLeafNode leaf => InsertSetsIntoLeaf(overlay, activeGroup, path, leaf, sets, start, end),
            PbtBranchNode branch => InsertSetsIntoBranch(overlay, activeGroup, path, branch, sets, start, end),
            _ => throw new ArgumentOutOfRangeException(nameof(current)),
        };

    private static ValueHash256 InsertSetsIntoLeaf(
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
                ? InsertSetsIntoNode(overlay, existingGroup, leftPath, leaf, sets, start, partition)
                : leaf.Hash;
            rightHash = BuildSubtree(overlay, group, rightPath, sets, partition, end);
        }
        else
        {
            leftHash = BuildSubtree(overlay, group, leftPath, sets, start, partition);
            rightHash = partition < end
                ? InsertSetsIntoNode(overlay, existingGroup, rightPath, leaf, sets, partition, end)
                : leaf.Hash;
        }

        PbtBranchNode split = new(common, leftHash, rightHash);
        overlay.Store(group, path, split);
        return split.Hash;
    }

    private static ValueHash256 InsertSetsIntoBranch(
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
                    ? InsertSetsIntoNode(overlay, existingGroup, leftPath, relocated, sets, start, partition)
                    : relocated.Hash;
                rightHash = BuildSubtree(overlay, group, rightPath, sets, partition, end);
            }
            else
            {
                leftHash = BuildSubtree(overlay, group, leftPath, sets, start, partition);
                rightHash = partition < end
                    ? InsertSetsIntoNode(overlay, existingGroup, rightPath, relocated, sets, partition, end)
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
            replacementLeft = InsertSets(overlay, group, leftPath, replacementLeft, sets, start, childPartition);
        }
        if (childPartition < end)
        {
            PbtNodePath rightPath = path.Append(branch.Prefix, 1);
            replacementRight = InsertSets(overlay, group, rightPath, replacementRight, sets, childPartition, end);
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
        int differingBit = int.MaxValue;
        for (int index = start + 1; index < end; index++)
        {
            PbtFullKey key = sets[index].Key;
            int difference = firstKey.FirstDifferingBit(key, path.BitDepth);
            if (difference == Math.Min(firstKey.BitLength, key.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(sets));
            differingBit = Math.Min(differingBit, difference);
        }
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
        matchingStart = start;
        matchingEnd = start;
        for (int index = start; index < end; index++)
        {
            PbtFullKey key = operations[index].Key;
            int available = key.BitLength - bitDepth;
            if (available <= branch.Prefix.BitCount ||
                MatchingPrefixBits(branch.Prefix, key, bitDepth) != branch.Prefix.BitCount)
                continue;

            (operations[matchingEnd], operations[index]) = (operations[index], operations[matchingEnd]);
            matchingEnd++;
        }
    }

    private static int PartitionByBit(PbtWriteOperation[] operations, int start, int end, int bitIndex)
    {
        int partition = start;
        for (int index = start; index < end; index++)
        {
            if (operations[index].Key.GetBit(bitIndex) != 0) continue;
            (operations[partition], operations[index]) = (operations[index], operations[partition]);
            partition++;
        }
        return partition;
    }

    private static void ValidateBatchKeys(PbtWriteOperation[] sets, int start, int end, int bitDepth)
    {
        while (end - start >= 2)
        {
            PbtFullKey anchor = sets[start].Key;
            int differingBit = int.MaxValue;
            for (int index = start + 1; index < end; index++)
            {
                PbtFullKey key = sets[index].Key;
                int difference = anchor.FirstDifferingBit(key, bitDepth);
                if (difference == Math.Min(anchor.BitLength, key.BitLength))
                    throw new ArgumentException("Tree keys must be prefix-free.", nameof(sets));
                differingBit = Math.Min(differingBit, difference);
            }

            int partition = PartitionByBit(sets, start, end, differingBit);
            int leftCount = partition - start;
            int rightCount = end - partition;
            if (leftCount < rightCount)
            {
                ValidateBatchKeys(sets, start, partition, differingBit + 1);
                start = partition;
            }
            else
            {
                ValidateBatchKeys(sets, partition, end, differingBit + 1);
                end = partition;
            }
            bitDepth = differingBit + 1;
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
