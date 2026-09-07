// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Applies complete-key mutations to a canonical compressed EIP-8297 tree.</summary>
public static class TrieUpdater
{
    private const int InPlaceSortThreshold = 32;
    private static readonly PbtNodePath RootPath = new([], 0);
    private static readonly PbtBitPrefix EmptyPrefix = new([], 0);

    /// <summary>Applies <paramref name="changes"/> and returns the resulting canonical root.</summary>
    /// <remarks>
    /// Effective mutations are folded through the tree as traversal-local partitioned ranges, so mutations
    /// sharing a path share one traversal. Each completed frame publishes its complete node group.
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

        PbtWriteOperation[] operations = [.. changes.Operations];
        using GroupFrameReader group = new(store, RootPath, metrics);
        PbtNode? root = group.Take(RootPath, allowAbsent: true);
        Subtree result = FoldMutations(store, metrics, group, new(root, RootPath), 0, operations);
        ValueHash256 hash = Place(group, result, RootPath);
        group.Flush();
        return hash;
    }

    private static Subtree FoldMutations(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrameReader ownerGroup,
        Subtree current,
        int depth,
        Span<PbtWriteOperation> operations)
    {
        current = Resolve(ownerGroup, current);
        if (operations.IsEmpty) return current;

        if (current.Node is not PbtBranchNode)
        {
            PbtLeafNode? leaf = (PbtLeafNode?)current.Node;
            int setCount = 0;
            for (int index = 0; index < operations.Length; index++)
            {
                PbtWriteOperation operation = operations[index];
                if (leaf is not null && operation.Key.Equals(leaf.Key))
                {
                    if (operation.Kind == PbtWriteOperationKind.Delete)
                    {
                        store.SetLeaf(leaf.Key, null);
                        current = default;
                    }
                    else
                    {
                        store.SetLeaf(operation.Key, operation.Value);
                        current = new(new PbtLeafNode(operation.Key, operation.Value), current.Path);
                    }
                }
                else if (operation.Kind == PbtWriteOperationKind.Set)
                {
                    (operations[setCount], operations[index]) = (operations[index], operations[setCount]);
                    setCount++;
                }
            }

            operations = operations[..setCount];
            if (operations.IsEmpty) return current;
            if (current.IsEmpty && operations.Length == 1)
                return SetLeaf(store, operations[0]);
        }
        else
        {
            // A shorter replacement can become valid when this same batch deletes the entire subtree.
            PbtWriteOperation? terminalSet = null;
            int remainingCount = 0;
            for (int index = 0; index < operations.Length; index++)
            {
                PbtWriteOperation operation = operations[index];
                if (operation.Key.BitLength == depth)
                {
                    if (operation.Kind == PbtWriteOperationKind.Set) terminalSet = operation;
                    continue;
                }
                (operations[remainingCount], operations[index]) = (operations[index], operations[remainingCount]);
                remainingCount++;
            }
            if (remainingCount != operations.Length)
            {
                Subtree remaining = FoldMutations(store, metrics, ownerGroup, current, depth, operations[..remainingCount]);
                if (terminalSet is not { } replacement) return remaining;
                if (!remaining.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
                return SetLeaf(store, replacement);
            }
        }

        int branchDepth = FindBranchDepth(current, depth, operations);
        int groupDepth = branchDepth / PbtFourLevelGroupGeometry.LevelsPerGroup * PbtFourLevelGroupGeometry.LevelsPerGroup;
        if (groupDepth != depth)
        {
            // Like the old chain fold, jump over an unbranched run without allocating virtual frames or prefixes.
            return FoldMutations(store, metrics, ownerGroup, current, groupDepth, operations);
        }

        if (ownerGroup.BitDepth == depth)
            return FoldBoundary(store, metrics, ownerGroup, current, depth, operations);

        using GroupFrameReader group = new(store, PbtNodePath.FromKey(operations[0].Key, depth), metrics);
        Subtree result = FoldBoundary(store, metrics, group, current, depth, operations);
        group.Flush();
        return result;
    }

    private static Subtree FoldBoundary(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupFrameReader group,
        Subtree current,
        int depth,
        Span<PbtWriteOperation> operations)
    {
        Span<int> offsets = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots + 1];
        BucketizeByGroupBoundary(operations, depth, offsets);
        RefList16<Subtree> boundaryBuffer = new(PbtFourLevelGroupGeometry.BoundarySlots);
        Span<Subtree> boundaries = boundaryBuffer.AsSpan();
        Decompose(group, current, depth, boundaries);

        for (int slot = 0; slot < boundaries.Length; slot++)
        {
            Span<PbtWriteOperation> bucket = operations[offsets[slot]..offsets[slot + 1]];
            if (bucket.IsEmpty) continue;
            boundaries[slot] = FoldMutations(
                store, metrics, group, boundaries[slot], depth + PbtFourLevelGroupGeometry.LevelsPerGroup, bucket);
        }

        for (int level = PbtFourLevelGroupGeometry.LevelsPerGroup - 1; level >= 0; level--)
        {
            int width = 1 << (PbtFourLevelGroupGeometry.LevelsPerGroup - level);
            for (int slot = 0; slot < boundaries.Length; slot += width)
            {
                Subtree left = boundaries[slot];
                Subtree right = boundaries[slot + width / 2];
                if (left.IsEmpty)
                {
                    boundaries[slot] = right;
                    continue;
                }
                if (right.IsEmpty) continue;

                PbtNodePath branchPath = BoundaryPath(group.GroupKey, slot, level);
                ValueHash256 leftHash = Place(group, left, branchPath.Append(EmptyPrefix, 0));
                ValueHash256 rightHash = Place(group, right, branchPath.Append(EmptyPrefix, 1));
                boundaries[slot] = new(new PbtBranchNode(EmptyPrefix, leftHash, rightHash), branchPath);
            }
        }

        // The root is handed up unplaced. Its old slot belongs to this frame, which may exit before placement.
        return Resolve(group, boundaries[0]);
    }

    private static void Decompose(GroupFrameReader group, Subtree current, int depth, Span<Subtree> boundaries)
    {
        if (current.IsEmpty) return;
        int boundaryDepth = depth + PbtFourLevelGroupGeometry.LevelsPerGroup;
        if (current.Node is null && current.Path!.BitDepth == boundaryDepth)
        {
            boundaries[BoundarySlot(current.Path.Path, depth)] = current;
            return;
        }

        current = Resolve(group, current);
        if (current.Node is PbtLeafNode leaf)
        {
            boundaries[BoundarySlot(leaf.Key, depth)] = current;
            return;
        }

        PbtBranchNode branch = (PbtBranchNode)current.Node!;
        int branchDepth = current.Path!.BitDepth + branch.Prefix.BitCount;
        if (branchDepth >= boundaryDepth)
        {
            int slot = 0;
            for (int bit = depth; bit < boundaryDepth; bit++)
                slot = (slot << 1) | PrefixBit(current, bit);
            boundaries[slot] = current;
            return;
        }

        Decompose(group, new(branch.LeftHash, current.Path.Append(branch.Prefix, 0)), depth, boundaries);
        Decompose(group, new(branch.RightHash, current.Path.Append(branch.Prefix, 1)), depth, boundaries);
    }

    private static int FindBranchDepth(Subtree current, int depth, Span<PbtWriteOperation> operations)
    {
        PbtFullKey firstKey = operations[0].Key;
        int branchDepth = firstKey.BitLength;
        for (int index = 1; index < operations.Length; index++)
        {
            PbtFullKey key = operations[index].Key;
            int difference = firstKey.FirstDifferingBit(key, depth);
            if (current.Node is not PbtBranchNode && difference == Math.Min(firstKey.BitLength, key.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
            branchDepth = Math.Min(branchDepth, difference);
        }

        if (current.Node is PbtLeafNode leaf)
        {
            int difference = leaf.Key.FirstDifferingBit(firstKey, depth);
            if (difference == Math.Min(leaf.Key.BitLength, firstKey.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
            branchDepth = Math.Min(branchDepth, difference);
        }
        else if (current.Node is PbtBranchNode branch)
        {
            int prefixStart = current.Path!.BitDepth;
            branchDepth = Math.Min(branchDepth, prefixStart + MatchingPrefixBits(branch.Prefix, firstKey, prefixStart));
        }
        return branchDepth;
    }

    private static Subtree SetLeaf(IPbtStore store, PbtWriteOperation operation)
    {
        store.SetLeaf(operation.Key, operation.Value);
        return new(new PbtLeafNode(operation.Key, operation.Value), null);
    }

    private static Subtree Resolve(GroupFrameReader group, Subtree subtree) =>
        subtree.IsEmpty || subtree.Node is not null ? subtree : new(group.Take(subtree.Path!)!, subtree.Path);

    private static ValueHash256 Place(GroupFrameReader group, Subtree subtree, PbtNodePath path)
    {
        if (subtree.IsEmpty) return default;
        if (subtree.Node is null && path.Equals(subtree.Path)) return subtree.Hash;
        subtree = Resolve(group, subtree);
        PbtNode node = subtree.Node!;
        if (node is PbtBranchNode branch && !path.Equals(subtree.Path))
        {
            int bitCount = subtree.Path!.BitDepth + branch.Prefix.BitCount - path.BitDepth;
            byte[] prefix = new byte[PbtBitPrefix.ByteCount(bitCount)];
            for (int index = 0; index < bitCount; index++)
                if (PrefixBit(subtree, path.BitDepth + index) != 0)
                    prefix[index >> 3] |= (byte)(1 << (7 - (index & 7)));
            node = new PbtBranchNode(PbtBitPrefix.TakeOwnership(prefix, bitCount), branch.LeftHash, branch.RightHash);
        }
        group.Store(path, node);
        return node.Hash;
    }

    private static int PrefixBit(Subtree subtree, int bit) => bit < subtree.Path!.BitDepth
        ? (subtree.Path.Path[bit >> 3] >> (7 - (bit & 7))) & 1
        : ((PbtBranchNode)subtree.Node!).Prefix.GetBit(bit - subtree.Path.BitDepth);

    private static PbtNodePath BoundaryPath(PbtNodePath groupKey, int slot, int level)
    {
        int depth = groupKey.BitDepth + level;
        byte[] path = new byte[(depth + 7) >> 3];
        groupKey.Path.CopyTo(path);
        for (int index = 0; index < level; index++)
        {
            if ((slot & (8 >> index)) == 0) continue;
            int bit = groupKey.BitDepth + index;
            path[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
        }
        return PbtNodePath.TakeOwnership(path, depth);
    }

    /// <summary>A boundary occupant or an unplaced result, retaining the original path of a compressed prefix.</summary>
    private readonly struct Subtree
    {
        internal Subtree(PbtNode? node, PbtNodePath? path)
        {
            Node = node;
            Path = path;
            Hash = node?.Hash ?? default;
        }

        internal Subtree(ValueHash256 hash, PbtNodePath path)
        {
            Hash = hash;
            Path = path;
        }

        internal PbtNode? Node { get; }
        internal PbtNodePath? Path { get; }
        internal ValueHash256 Hash { get; }
        internal bool IsEmpty => Hash == default;
    }

    internal static void BucketizeByGroupBoundary(Span<PbtWriteOperation> operations, int groupDepth, Span<int> offsets)
    {
        if (operations.Length >= InPlaceSortThreshold)
        {
            BucketizeLarge(operations, groupDepth, offsets);
            return;
        }

        if (operations.Length is 2 or 3)
            BucketizeTiny(operations, groupDepth);
        else if (operations.Length > 3)
            BucketizeSmall(operations, groupDepth);

        offsets[..(PbtFourLevelGroupGeometry.BoundarySlots + 1)].Clear();
        for (int index = 0; index < operations.Length; index++)
            offsets[BoundarySlot(operations[index].Key, groupDepth) + 1]++;
        for (int bucket = 0; bucket < PbtFourLevelGroupGeometry.BoundarySlots; bucket++)
            offsets[bucket + 1] += offsets[bucket];
    }

    private static void BucketizeTiny(Span<PbtWriteOperation> operations, int groupDepth)
    {
        int firstSlot = BoundarySlot(operations[0].Key, groupDepth);
        int secondSlot = BoundarySlot(operations[1].Key, groupDepth);
        if (firstSlot > secondSlot)
        {
            (operations[0], operations[1]) = (operations[1], operations[0]);
            (firstSlot, secondSlot) = (secondSlot, firstSlot);
        }
        if (operations.Length == 2) return;

        int thirdSlot = BoundarySlot(operations[2].Key, groupDepth);
        if (secondSlot > thirdSlot)
        {
            (operations[1], operations[2]) = (operations[2], operations[1]);
            secondSlot = thirdSlot;
        }
        if (firstSlot > secondSlot)
            (operations[0], operations[1]) = (operations[1], operations[0]);
    }

    private static void BucketizeSmall(Span<PbtWriteOperation> operations, int groupDepth)
    {
        Span<(int Index, int Slot)> sorted = stackalloc (int, int)[operations.Length];
        for (int index = 0; index < operations.Length; index++)
            sorted[index] = (index, BoundarySlot(operations[index].Key, groupDepth));

        for (int index = 1; index < sorted.Length; index++)
        {
            (int Index, int Slot) entry = sorted[index];
            int previous = index - 1;
            while (previous >= 0 && sorted[previous].Slot > entry.Slot)
            {
                sorted[previous + 1] = sorted[previous];
                previous--;
            }
            sorted[previous + 1] = entry;
        }

        for (int index = 0; index < sorted.Length; index++)
        {
            if (sorted[index].Index == index) continue;

            PbtWriteOperation operation = operations[index];
            int destination = index;
            do
            {
                int source = sorted[destination].Index;
                sorted[destination].Index = destination;
                if (source == index)
                {
                    operations[destination] = operation;
                    break;
                }
                operations[destination] = operations[source];
                destination = source;
            } while (true);
        }
    }

    private static void BucketizeLarge(Span<PbtWriteOperation> operations, int groupDepth, Span<int> offsets)
    {
        Span<int> counts = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        counts.Clear();
        int usedMask = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            int bucket = BoundarySlot(operations[index].Key, groupDepth);
            counts[bucket]++;
            usedMask |= 1 << bucket;
        }
        offsets[0] = 0;
        for (int bucket = 0; bucket < PbtFourLevelGroupGeometry.BoundarySlots; bucket++)
            offsets[bucket + 1] = offsets[bucket] + counts[bucket];
        if (BitOperations.IsPow2(usedMask)) return;

        Span<int> next = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        offsets[..PbtFourLevelGroupGeometry.BoundarySlots].CopyTo(next);

        for (int mask = usedMask; mask != 0; mask &= mask - 1)
        {
            int bucket = BitOperations.TrailingZeroCount(mask);
            int end = offsets[bucket + 1];
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

    private static int BoundarySlot(PbtFullKey key, int groupDepth) => BoundarySlot(key.Bytes, groupDepth);

    private static int BoundarySlot(ReadOnlySpan<byte> key, int groupDepth)
    {
        byte value = key[groupDepth >> 3];
        return (value >> (4 - (groupDepth & 4))) & 0x0F;
    }

    private static int MatchingPrefixBits(PbtBitPrefix prefix, PbtFullKey key, int keyOffset)
    {
        int available = key.BitLength - keyOffset;
        int count = Math.Min(prefix.BitCount, available);
        ReadOnlySpan<byte> prefixBytes = prefix.Bytes;
        ReadOnlySpan<byte> keyBytes = key.Bytes;
        int keyBitOffset = keyOffset & 7;
        int index = 0;
        for (; index + 8 <= count; index += 8)
        {
            int keyByteIndex = (keyOffset + index) >> 3;
            int keyByte = keyBitOffset == 0
                ? keyBytes[keyByteIndex]
                : ((keyBytes[keyByteIndex] << 8) | keyBytes[keyByteIndex + 1]) >> (8 - keyBitOffset);
            int difference = prefixBytes[index >> 3] ^ (keyByte & 0xFF);
            if (difference != 0) return index + BitOperations.LeadingZeroCount((uint)difference) - 24;
        }
        while (index < count && prefix.GetBit(index) == key.GetBit(keyOffset + index)) index++;
        return index;
    }

    private sealed class GroupFrameReader : IDisposable
    {
        private readonly IPbtStore _store;
        private readonly TrieUpdaterMetrics? _metrics;
        private readonly RefCountingMemory? _lease;
        private OffsetBuffer _offsets;
        private LengthBuffer _lengths;
        private NodeBuffer _nodes;
        private uint _changed;

        internal GroupFrameReader(IPbtStore store, PbtNodePath groupKey, TrieUpdaterMetrics? metrics)
        {
            _store = store;
            GroupKey = groupKey;
            _metrics = metrics;
            metrics?.IncrementGroupFrameResolutions();
            metrics?.IncrementPhysicalGroupFetches();
            _lease = store.GetNodeGroup(groupKey);
            if (_lease is null) return;
            try
            {
                metrics?.IncrementGroupParses();
                PbtNodeGroupReader reader = new(groupKey, _lease.GetSpan());
                for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
                {
                    if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
                    if (!reader.TryGetNodeRange(position, out int offset, out int length)) continue;
                    _offsets[position] = offset;
                    _lengths[position] = length;
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal PbtNodePath GroupKey { get; }
        internal int BitDepth => GroupKey.BitDepth;

        internal PbtNode? Take(PbtNodePath path, bool allowAbsent = false)
        {
            int position = Position(path);
            PbtNode? node = _nodes[position];
            if ((_changed & (1U << position)) == 0 && _lengths[position] != 0)
                node ??= PbtNodeCodec.Decode(_lease!.GetSpan().Slice(_offsets[position], _lengths[position]));
            if (node is null && !allowAbsent) throw new InvalidDataException("A referenced PBT node is missing.");
            _nodes[position] = null;
            _changed |= 1U << position;
            return node;
        }

        internal void Store(PbtNodePath path, PbtNode node)
        {
            int position = Position(path);
            _nodes[position] = node;
            _changed |= 1U << position;
        }

        internal void Flush()
        {
            if (_changed == 0) return;
            ReadOnlyMemory<byte>[] encodings = new ReadOnlyMemory<byte>[PbtNodeGroupCodec.PositionCount];
            bool[] present = new bool[PbtNodeGroupCodec.PositionCount];
            int changedNodes = 0;
            bool anyPresent = false;
            for (int position = 0; position < encodings.Length; position++)
            {
                ReadOnlyMemory<byte> previous = _lengths[position] == 0
                    ? default
                    : _lease!.Memory.Slice(_offsets[position], _lengths[position]);
                ReadOnlyMemory<byte> encoding = previous;
                if ((_changed & (1U << position)) != 0)
                {
                    PbtNode? node = _nodes[position];
                    encoding = node is null ? default : PbtNodeCodec.Encode(node);
                    if (!previous.Span.SequenceEqual(encoding.Span)) changedNodes++;
                }
                encodings[position] = encoding;
                present[position] = !encoding.IsEmpty;
                anyPresent |= present[position];
            }
            if (changedNodes == 0) return;

            if (!anyPresent)
            {
                _store.SetNodeGroup(GroupKey, null);
            }
            else
            {
                BufferWriter writer = new(PooledRefCountingMemoryProvider.Instance);
                try
                {
                    PbtNodeGroupCodec.Encode(ref writer, GroupKey, encodings, present);
                    using RefCountingMemory payload = writer.Detach()!;
                    _store.SetNodeGroup(GroupKey, payload);
                }
                finally
                {
                    writer.Dispose();
                }
            }
            _metrics?.AddEmittedNodeWrites(changedNodes);
        }

        private int Position(PbtNodePath path)
        {
            int completeBytes = BitDepth >> 3;
            int remainingBits = BitDepth & 7;
            if (PbtFourLevelGroupGeometry.GroupDepthOf(path.BitDepth) != BitDepth
                || !path.Path[..completeBytes].SequenceEqual(GroupKey.Path[..completeBytes])
                || (remainingBits != 0 && ((path.Path[completeBytes] ^ GroupKey.Path[completeBytes]) & 0xF0) != 0))
                throw new InvalidOperationException("The PBT node does not belong to the active group.");
            return PbtFourLevelGroupGeometry.PositionOf(path);
        }

        public void Dispose() => ((IDisposable?)_lease)?.Dispose();

        [InlineArray(PbtNodeGroupCodec.PositionCount)]
        private struct OffsetBuffer
        {
            private int _element;
        }

        [InlineArray(PbtNodeGroupCodec.PositionCount)]
        private struct LengthBuffer
        {
            private int _element;
        }

        [InlineArray(PbtNodeGroupCodec.PositionCount)]
        private struct NodeBuffer
        {
            private PbtNode? _element;
        }
    }
}

internal sealed class TrieUpdaterMetrics
{
    internal int PhysicalGroupFetches { get; private set; }
    internal int GroupParses { get; private set; }
    internal int GroupFrameResolutions { get; private set; }
    internal int EmittedNodeWrites { get; private set; }

    internal void IncrementPhysicalGroupFetches() => PhysicalGroupFetches++;
    internal void IncrementGroupParses() => GroupParses++;
    internal void IncrementGroupFrameResolutions() => GroupFrameResolutions++;
    internal void AddEmittedNodeWrites(int count) => EmittedNodeWrites += count;
}
