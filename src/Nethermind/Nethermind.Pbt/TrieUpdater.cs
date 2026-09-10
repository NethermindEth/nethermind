// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

/// <summary>Applies complete-key mutations to a canonical compressed EIP-8297 tree.</summary>
public static partial class TrieUpdater
{
    internal static int GetBit(ReadOnlySpan<byte> bytes, int bit) => (bytes[bit >> 3] >> (7 - (bit & 7))) & 1;

    internal enum NodeKind : byte { Empty, Reference, Original, Leaf, Branch }

    /// <summary>The touched buckets and range knowledge established by partitioning.</summary>
    internal readonly ref struct PartitionOutcome(int usedMask, ReadOnlySpan<int> counts, BucketPlan plan)
    {
        internal int UsedMask { get; } = usedMask;
        /// <summary>Non-empty bucket counts in ascending slot order.</summary>
        internal ReadOnlySpan<int> Counts { get; } = counts;
        internal BucketPlan Plan { get; } = plan;
    }

    internal static int BoundarySlot<TKey>(TKey key, int groupDepth) where TKey : struct, IPbtKey<TKey> => BoundarySlot(key.Bytes, groupDepth);

    internal static int BoundarySlot(ReadOnlySpan<byte> key, int groupDepth)
    {
        byte value = key[groupDepth >> 3];
        return (value >> (4 - (groupDepth & 4))) & 0x0F;
    }

}

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : struct, IPbtKey<TKey>
    where TPath : class, IPbtNodePath<TPath>
{
    private static readonly TPath RootPath = TPath.Create([], 0);

    /// <summary>Applies <paramref name="changes"/> and returns the resulting canonical root.</summary>
    /// <remarks>
    /// Effective mutations are folded through the tree as traversal-local partitioned ranges, so mutations
    /// sharing a path share one traversal. Each completed frame publishes its complete node group.
    /// </remarks>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<TKey> changes) =>
        UpdateRoot(store, currentRoot, changes, null);

    internal static ValueHash256 UpdateRoot(
        IPbtStore store,
        in ValueHash256 currentRoot,
        PbtWriteBatch<TKey> changes,
        TrieUpdaterMetrics? metrics,
        IRefCountingMemoryProvider? memoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        BucketPlan plan = changes.Plan;
        changes.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table);
        using ArrayPoolList<PbtWriteOperation<TKey>> ownedOperations = operations;
        using ArrayPoolList<int> ownedTable = table;
        return UpdateRoot(store, currentRoot, operations.AsSpan(), changes.ShardNibbleIndex == 0 ? plan : default, metrics, memoryProvider);
    }

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatchSet<TKey> changes, TrieUpdaterMetrics? metrics = null, IRefCountingMemoryProvider? memoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        changes.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> precalculated);
        using ArrayPoolList<PbtWriteOperation<TKey>> ownedOperations = operations;
        using ArrayPoolList<int> ownedTable = precalculated;
        return UpdateRoot(store, currentRoot, operations.AsSpan(), new(precalculated.AsSpan(), 0, 0, false, false), metrics, memoryProvider);
    }

    private static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, Span<PbtWriteOperation<TKey>> operations, BucketPlan plan, TrieUpdaterMetrics? metrics, IRefCountingMemoryProvider? memoryProvider)
    {
        if (operations.IsEmpty) return currentRoot;
        memoryProvider ??= PooledRefCountingMemoryProvider.Instance;
        GroupFrameReader<TKey, TPath> reader = new(store, RootPath, metrics);
        try
        {
            using PbtNodeGroupWriter writer = new(RootPath, memoryProvider);
            Subtree root = reader.Take(writer, RootPath, allowAbsent: true);
            Subtree result = default;
            try
            {
                result = FoldMutations(store, metrics, ref reader, writer, memoryProvider, ref root, operations, plan);
                ValueHash256 hash = writer.Write(ref reader, PbtFourLevelGroupGeometry.RootPosition, 0, ref result);
                Flush(store, metrics, ref reader, writer);
                return hash;
            }
            finally
            {
                root.Dispose();
                result.Dispose();
            }
        }
        finally { reader.Dispose(); }
    }

    private static Subtree FoldMutations(IPbtStore store, TrieUpdaterMetrics? metrics, ref GroupFrameReader<TKey, TPath> ownerReader, PbtNodeGroupWriter ownerWriter, IRefCountingMemoryProvider memoryProvider,
        ref Subtree input, Span<PbtWriteOperation<TKey>> operations, BucketPlan plan)
    {
        Subtree current = Subtree.Move(ref input);
        try
        {
            int depth = plan.Depth;
            ownerReader.Resolve(ownerWriter, ref current);
            if (operations.IsEmpty) return Subtree.Move(ref current);

            if (current.IsEmpty)
            {
                if (operations.Length == 1)
                    return operations[0].Value == default ? default : new Subtree(operations[0]);
            }
            else if (current.IsLeaf)
            {
                if (operations.Length == 1)
                {
                    PbtWriteOperation<TKey> operation = operations[0];
                    if (operation.Key.Equals(current.Key))
                        return operation.Value == default ? default : new Subtree(operation, current.Path);
                    if (operation.Value == default) return Subtree.Move(ref current);
                }
            }

            if (depth > 0 && (depth & 7) == 0)
            {
                int terminalIndex = -1;
                for (int index = 0; index < operations.Length; index++)
                {
                    if (operations[index].Key.BitLength != depth) continue;
                    terminalIndex = index;
                    break;
                }
                bool hasTerminalLeaf = current.IsLeaf && current.Key.BitLength == depth;
                if (terminalIndex >= 0 || hasTerminalLeaf)
                {
                    // EIP-8297 prefix freedom applies to surviving keys, after both buckets have been folded.
                    Subtree terminal = hasTerminalLeaf ? Subtree.Move(ref current) : default;
                    Subtree descendants = default;
                    try
                    {
                        if (terminalIndex >= 0)
                        {
                            PbtWriteOperation<TKey> operation = operations[terminalIndex];
                            operations[..terminalIndex].CopyTo(operations[1..]);
                            operations[0] = operation;
                            terminal = FoldMutations(store, metrics, ref ownerReader, ownerWriter, memoryProvider, ref terminal, operations[..1], plan);
                            operations = operations[1..];
                            plan = plan.AfterFiltering(preservesOrder: true);
                        }
                        descendants = FoldMutations(store, metrics, ref ownerReader, ownerWriter, memoryProvider, ref current, operations, plan);
                        if (terminal.IsEmpty) return Subtree.Move(ref descendants);
                        if (!descendants.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
                        return Subtree.Move(ref terminal);
                    }
                    finally
                    {
                        terminal.Dispose();
                        descendants.Dispose();
                    }
                }
            }

            plan = plan.EstablishRangeKnowledge(current.IsEmpty || current.IsLeaf, operations, metrics);
            Span<byte> buffer = stackalloc byte[plan.GetBufferSize(operations.Length)];
            bool hasComputedPartition = false;
            scoped PartitionOutcome partition = default;
            // Partitioning can discover a shared prefix that lets traversal skip groups; reuse the partition if no jump is possible.
            if (plan.Precalculated.IsEmpty && plan.BranchDepth <= depth)
            {
                partition = plan.WithBuffer(buffer).BucketSort(operations, metrics);
                plan = new(default, depth, partition.Plan.BranchDepth, partition.Plan.IsSorted, partition.Plan.PrefixesValidated);
                hasComputedPartition = true;
            }
            int branchDepth = FindBranchDepth(current, operations[0].Key, plan);
            int groupDepth = branchDepth / PbtFourLevelGroupGeometry.LevelsPerGroup * PbtFourLevelGroupGeometry.LevelsPerGroup;
            if (groupDepth != depth)
            {
                if (!plan.Precalculated.IsEmpty && BitOperations.IsPow2(plan.Precalculated[0]))
                {
                    metrics?.IncrementPrecalculatedLevels();
                    return FoldMutations(store, metrics, ref ownerReader, ownerWriter, memoryProvider, ref current, operations,
                        plan.ForChild());
                }

                // The range's prefix survives the jump; the existing subtree only limits how far we can jump.
                return FoldMutations(store, metrics, ref ownerReader, ownerWriter, memoryProvider, ref current, operations, plan.AfterJump(groupDepth));
            }

            if (ownerReader.BitDepth == depth)
                return hasComputedPartition
                    ? FoldBoundaryFromPartition(store, metrics, ref ownerReader, ownerWriter, memoryProvider, ref current, operations, partition)
                    : FoldBoundary(store, metrics, ref ownerReader, ownerWriter, memoryProvider, ref current, operations, plan);

            GroupFrameReader<TKey, TPath> reader = new(store, PbtPathOperations.FromKey<TPath>(operations[0].Key.Bytes, depth), metrics);
            try
            {
                using PbtNodeGroupWriter writer = new(reader.GroupKey, memoryProvider);
                Subtree result = hasComputedPartition
                    ? FoldBoundaryFromPartition(store, metrics, ref reader, writer, memoryProvider, ref current, operations, partition)
                    : FoldBoundary(store, metrics, ref reader, writer, memoryProvider, ref current, operations, plan);
                try
                {
                    Flush(store, metrics, ref reader, writer);
                    return Subtree.Move(ref result);
                }
                finally { result.Dispose(); }
            }
            finally { reader.Dispose(); }
        }
        finally { current.Dispose(); }
    }

    internal static Subtree Compose(ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter writer, TrieUpdaterMetrics? metrics, Span<Subtree> boundaries, int touchedMask)
    {
        int occupied = 0;
        for (int slot = 0; slot < boundaries.Length; slot++)
        {
            // Consume original boundary positions before ordered emission can pass their old locations.
            reader.Resolve(writer, ref boundaries[slot]);
            if (!boundaries[slot].IsEmpty) occupied |= 1 << slot;
        }
        if (occupied == 0) return default;

        ComposeFrameBuffer frames = default;
        int frameCount = 1;
        frames[0] = new(occupied, default);
        Subtree result = default;
        try
        {
            while (frameCount != 0)
            {
                ref ComposeFrame frame = ref frames[frameCount - 1];
                int halfWidth = frame.Path.Width / 2;
                if (frame.Stage == ComposeStage.LeftCompleted)
                {
                    // The left root must be emitted before any descendants of the right subtree.
                    frame.LeftHash = writer.Write(ref reader, frame.Path.Position - frame.Path.Width, reader.GroupKey.BitDepth + frame.Path.Length + 1, ref result);
                    frame.Stage = ComposeStage.RightCompleted;
                    if (halfWidth == 1)
                        result = Subtree.Move(ref boundaries[frame.Path.Slot + 1]);
                    else
                        frames[frameCount++] = new(frame.Occupied >> halfWidth, frame.Path.Right);
                    continue;
                }
                if (frame.Stage == ComposeStage.RightCompleted)
                {
                    ValueHash256 rightHash = writer.Write(ref reader, frame.Path.Position - 1, reader.GroupKey.BitDepth + frame.Path.Length + 1, ref result);
                    TPath branchPath = BoundaryPath(reader.GroupKey, frame.Path.Slot, frame.Path.Length);
                    result = new Subtree(branchPath, frame.LeftHash, rightHash);
                    frameCount--;
                    continue;
                }
                int rangeMask = ((1 << frame.Path.Width) - 1) << frame.Path.Slot;
                if ((touchedMask & rangeMask) == 0 && TryCopyUnchangedSubtree(ref reader, writer, metrics, frame.Path, out result))
                {
                    Dispose(boundaries.Slice(frame.Path.Slot, frame.Path.Width));
                    frameCount--;
                    continue;
                }
                if (BitOperations.IsPow2(frame.Occupied))
                {
                    result = Subtree.Move(ref boundaries[frame.Path.Slot + BitOperations.TrailingZeroCount(frame.Occupied)]);
                    frameCount--;
                    continue;
                }

                int leftMask = frame.Occupied & ((1 << halfWidth) - 1);
                int rightMask = frame.Occupied >> halfWidth;
                if (leftMask == 0)
                {
                    frame = new(rightMask, frame.Path.Right);
                    continue;
                }
                if (rightMask == 0)
                {
                    frame = new(leftMask, frame.Path.Left);
                    continue;
                }

                frame.Stage = ComposeStage.LeftCompleted;
                if (halfWidth == 1)
                    result = Subtree.Move(ref boundaries[frame.Path.Slot]);
                else
                    frames[frameCount++] = new(leftMask, frame.Path.Left);
            }
            return Subtree.Move(ref result);
        }
        finally { result.Dispose(); }
    }

    private enum ComposeStage : byte { Descend, LeftCompleted, RightCompleted }

    private struct ComposeFrame(int occupied, NodeGroupPath path)
    {
        internal int Occupied = occupied;
        internal NodeGroupPath Path = path;
        internal ComposeStage Stage;
        internal ValueHash256 LeftHash;
    }

    [InlineArray(PbtFourLevelGroupGeometry.LevelsPerGroup)]
    private struct ComposeFrameBuffer
    {
        private ComposeFrame _element;
    }

    internal static void Decompose(ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter writer, ref Subtree current, int depth, Span<Subtree> boundaries)
    {
        if (current.IsEmpty) return;
        int boundaryDepth = depth + PbtFourLevelGroupGeometry.LevelsPerGroup;
        if (current.IsReference && current.Path!.BitDepth == boundaryDepth)
        {
            boundaries[BoundarySlot(current.Path.Path, depth)] = Subtree.Move(ref current);
            return;
        }

        reader.Resolve(writer, ref current);
        if (!current.IsEmpty && current.IsLeaf)
        {
            boundaries[BoundarySlot(current.Key.Bytes, depth)] = Subtree.Move(ref current);
            return;
        }

        int branchDepth = current.Path!.BitDepth + current.PrefixBitCount;
        if (branchDepth >= boundaryDepth)
        {
            int slot = 0;
            for (int bit = depth; bit < boundaryDepth; bit++)
                slot = (slot << 1) | PrefixBit(current, bit);
            boundaries[slot] = Subtree.Move(ref current);
            return;
        }

        Subtree left = new(current.LeftHash, PbtPathOperations.Append<TPath>(current.Path, current.Prefix, current.PrefixBitCount, 0));
        Subtree right = new(current.RightHash, PbtPathOperations.Append<TPath>(current.Path, current.Prefix, current.PrefixBitCount, 1));
        current.Dispose();
        try
        {
            Decompose(ref reader, writer, ref left, depth, boundaries);
            Decompose(ref reader, writer, ref right, depth, boundaries);
        }
        finally
        {
            left.Dispose();
            right.Dispose();
        }
    }

    private static int FindBranchDepth(Subtree current, TKey firstKey, BucketPlan plan)
    {
        int branchDepth = plan.BranchDepth;
        if (!current.IsEmpty && current.IsLeaf)
        {
            TKey leafKey = current.Key;
            int difference = leafKey.FirstDifferingBit(firstKey, plan.Depth);
            branchDepth = Math.Min(branchDepth, difference);
        }
        else if (!current.IsEmpty)
        {
            int prefixStart = current.Path!.BitDepth;
            branchDepth = Math.Min(branchDepth, prefixStart + MatchingPrefixBits(current.Prefix, current.PrefixBitCount, firstKey, prefixStart));
        }
        return branchDepth;
    }

    internal static void Dispose(Span<Subtree> subtrees)
    {
        foreach (ref Subtree subtree in subtrees) subtree.Dispose();
    }

    private static int PrefixBit(Subtree subtree, int bit) => bit < subtree.Path!.BitDepth
        ? (subtree.Path.Path[bit >> 3] >> (7 - (bit & 7))) & 1
        : GetBit(subtree.Prefix, bit - subtree.Path.BitDepth);

    private static TPath BoundaryPath(TPath groupKey, int slot, int level)
    {
        if (level == 0) return groupKey;
        int depth = groupKey.BitDepth + level;
        Span<byte> path = stackalloc byte[(depth + 7) >> 3];
        path.Clear();
        groupKey.Path.CopyTo(path);
        path[^1] |= (byte)((slot & (0xF << (4 - level))) << (4 - (groupKey.BitDepth & 4)));
        return TPath.Create(path, depth);
    }

    /// <summary>A boundary occupant or an unplaced result, retaining the original path of a compressed prefix.</summary>
    /// <remarks>Value copies are borrowed; only Move transfers ownership of the single lease.</remarks>
    internal struct Subtree : IDisposable
    {
        private RefCountingMemory? _lease;
        private readonly NodeKind _kind;
        private readonly ReadOnlyMemory<byte> _encoding;
        private readonly TKey _key;
        private readonly ValueHash256 _valueOrLeft;
        private readonly ValueHash256 _right;

        internal Subtree(RefCountingMemory lease, ReadOnlyMemory<byte> encoding, TPath path)
        {
            _lease = lease;
            _kind = NodeKind.Original;
            _encoding = encoding;
            Path = path;
        }

        internal Subtree(ValueHash256 hash, TPath path)
        {
            _kind = hash == default ? NodeKind.Empty : NodeKind.Reference;
            Path = path;
        }

        internal Subtree(PbtWriteOperation<TKey> operation, TPath? path = null)
        {
            _kind = NodeKind.Leaf;
            _key = operation.Key;
            _valueOrLeft = operation.Value;
            Path = path;
        }

        internal Subtree(TPath path, in ValueHash256 left, in ValueHash256 right)
        {
            _kind = NodeKind.Branch;
            _valueOrLeft = left;
            _right = right;
            Path = path;
        }

        private Subtree(RefCountingMemory? lease, NodeKind kind, ReadOnlyMemory<byte> encoding, TKey key,
            ValueHash256 valueOrLeft, ValueHash256 right, TPath? path)
        {
            _lease = lease;
            _kind = kind;
            _encoding = encoding;
            _key = key;
            _valueOrLeft = valueOrLeft;
            _right = right;
            Path = path;
        }

        internal static Subtree TakeFrom<TSourceKey, TSourcePath>(ref TrieUpdater<TSourceKey, TSourcePath>.Subtree source)
            where TSourceKey : struct, IPbtKey<TSourceKey>
            where TSourcePath : class, IPbtNodePath<TSourcePath>
        {
            TKey key = default;
            if (source.IsLeaf)
            {
                TSourceKey sourceKey = source.Key;
                key = TKey.Create(sourceKey.Bytes);
            }
            TPath? path = source.Path is { } sourcePath
                ? sourcePath as TPath ?? TPath.Create(sourcePath.Path, sourcePath.BitDepth)
                : null;
            Subtree result = new(source._lease, source._kind, source._encoding, key,
                source._valueOrLeft, source._right, path);
            source = default;
            return result;
        }

        private readonly PbtNodeReader Reader => new(_encoding.Span);
        internal readonly TPath? Path { get; }
        internal readonly bool IsEmpty => _kind == NodeKind.Empty;
        internal readonly bool IsReference => _kind == NodeKind.Reference;
        internal readonly bool IsLeaf => _kind == NodeKind.Leaf || (_kind == NodeKind.Original && Reader.IsLeaf);
        internal readonly TKey Key => _kind == NodeKind.Leaf ? _key : TKey.Create(Reader.Key);
        internal readonly ReadOnlySpan<byte> Prefix => _kind == NodeKind.Branch ? [] : Reader.Prefix;
        internal readonly int PrefixBitCount => _kind == NodeKind.Branch ? 0 : Reader.PrefixBitCount;
        internal readonly ValueHash256 LeftHash => _kind == NodeKind.Branch ? _valueOrLeft : Reader.LeftHash;
        internal readonly ValueHash256 RightHash => _kind == NodeKind.Branch ? _right : Reader.RightHash;

        internal readonly int EncodedLength(int depth) => IsLeaf
            ? (_kind == NodeKind.Leaf ? 3 + _key.Length + 32 : _encoding.Length)
            : 3 + PbtBitPrefix.ByteCount(Path!.BitDepth + PrefixBitCount - depth) + 64;

        internal readonly ValueHash256 Encode(Span<byte> encoding, int depth)
        {
            if (_kind == NodeKind.Original && (IsLeaf || depth == Path!.BitDepth))
            {
                _encoding.Span.CopyTo(encoding);
                return PbtNodeCodec.Hash(Reader);
            }
            if (IsLeaf)
            {
                PbtNodeCodec.EncodeLeaf(encoding, _key, _valueOrLeft.Bytes);
                return PbtNodeCodec.Hash(new PbtNodeReader(encoding));
            }

            // EIP-8297 promotion absorbs skipped path bits; the source anchor remains unchanged until placement.
            int pathDepth = Path!.BitDepth;
            int bitCount = pathDepth + PrefixBitCount - depth;
            PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, LeftHash, RightHash);
            Span<byte> prefix = encoding.Slice(3, PbtBitPrefix.ByteCount(bitCount));
            int pathBits = Math.Max(0, pathDepth - depth);
            PbtBitPrefix.CopyBits(Path.Path, Math.Min(depth, pathDepth), pathBits, prefix, 0);
            int prefixOffset = Math.Max(0, depth - pathDepth);
            PbtBitPrefix.CopyBits(Prefix, prefixOffset, bitCount - pathBits, prefix, pathBits);
            return Blake3Hash.Hash(encoding);
        }

        internal static Subtree Move(ref Subtree source)
        {
            Subtree result = source;
            source = default;
            return result;
        }

        public void Dispose()
        {
            RefCountingMemory? lease = _lease;
            this = default;
            ((IDisposable?)lease)?.Dispose();
        }
    }

    private static int MatchingPrefixBits(ReadOnlySpan<byte> prefixBytes, int prefixBitCount, TKey key, int keyOffset)
    {
        int available = key.BitLength - keyOffset;
        int count = Math.Min(prefixBitCount, available);
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
        while (index < count && GetBit(prefixBytes, index) == key.GetBit(keyOffset + index)) index++;
        return index;
    }

    private static bool TryCopyUnchangedSubtree(ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter writer, TrieUpdaterMetrics? metrics, NodeGroupPath path, out Subtree root)
    {
        int position = path.Position;
        root = default;
        // Only a consumed source root proves this range belongs to the subtree being recomposed.
        if ((reader.Taken & (1U << position)) == 0) return false;
        root = reader.Acquire(position, BoundaryPath(reader.GroupKey, path.Slot, path.Length));
        if (root.IsEmpty) return false;
        int startPosition = position - 2 * path.Width + 2;
        writer.CopyUntouchedBefore(ref reader, startPosition);
        // Placement may promote the root and extend its compressed prefix, so copy only its descendants.
        int copiedNodes = reader.CopyRange(writer, startPosition, position);
        if (copiedNodes != 0) metrics?.AddBulkCopy(copiedNodes);
        writer.NextPosition = position;
        return true;
    }

    internal static void Flush(IPbtStore store, TrieUpdaterMetrics? metrics, ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter writer)
    {
        if (reader.Taken == 0 && writer.Availability == 0) return;
        writer.CopyUntouchedBefore(ref reader, PbtNodeGroupCodec.PositionCount);
        if (writer.ChangedNodes == 0) return;

        using RefCountingMemory? payload = writer.Detach();
        store.SetNodeGroup(reader.GroupKey, payload);
        metrics?.AddEmittedNodeWrites(writer.ChangedNodes);
    }
}

internal sealed class TrieUpdaterMetrics
{
    internal int PrecalculatedLevels { get; private set; }
    internal int SortedLevels { get; private set; }
    internal int FullKeySorts { get; private set; }
    internal int RadixPartitions { get; private set; }
    internal int OperationPrefixComparisons { get; private set; }
    internal int SynthesizedSingleBuckets { get; private set; }
    internal int PhysicalGroupFetches { get; private set; }
    internal int GroupParses { get; private set; }
    internal int GroupFrameResolutions { get; private set; }
    internal int EmittedNodeWrites { get; private set; }
    internal int BulkCopiedNodes { get; private set; }
    internal int BulkCopyOperations { get; private set; }

    internal void Add(TrieUpdaterMetrics metrics)
    {
        PrecalculatedLevels += metrics.PrecalculatedLevels;
        SortedLevels += metrics.SortedLevels;
        FullKeySorts += metrics.FullKeySorts;
        RadixPartitions += metrics.RadixPartitions;
        OperationPrefixComparisons += metrics.OperationPrefixComparisons;
        SynthesizedSingleBuckets += metrics.SynthesizedSingleBuckets;
        PhysicalGroupFetches += metrics.PhysicalGroupFetches;
        GroupParses += metrics.GroupParses;
        GroupFrameResolutions += metrics.GroupFrameResolutions;
        EmittedNodeWrites += metrics.EmittedNodeWrites;
        BulkCopiedNodes += metrics.BulkCopiedNodes;
        BulkCopyOperations += metrics.BulkCopyOperations;
    }

    internal void AddBulkCopy(int nodes)
    {
        BulkCopiedNodes += nodes;
        BulkCopyOperations++;
    }
    internal void IncrementPrecalculatedLevels() => PrecalculatedLevels++;
    internal void IncrementSortedLevels() => SortedLevels++;
    internal void IncrementFullKeySorts() => FullKeySorts++;
    internal void IncrementRadixPartitions() => RadixPartitions++;
    internal void IncrementOperationPrefixComparisons() => OperationPrefixComparisons++;
    internal void IncrementSynthesizedSingleBuckets() => SynthesizedSingleBuckets++;
    internal void IncrementPhysicalGroupFetches() => PhysicalGroupFetches++;
    internal void IncrementGroupParses() => GroupParses++;
    internal void IncrementGroupFrameResolutions() => GroupFrameResolutions++;
    internal void AddEmittedNodeWrites(int count) => EmittedNodeWrites += count;
}
