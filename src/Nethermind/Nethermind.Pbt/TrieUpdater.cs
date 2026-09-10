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

    internal enum NodeKind : byte { Empty, Original, Leaf, Branch }

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
    where TPath : struct, IPbtNodePath<TPath>
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
        return UpdateRoot(store, currentRoot, operations.AsSpan(), new(precalculated.AsSpan(), 0, false), metrics, memoryProvider);
    }

    private static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, Span<PbtWriteOperation<TKey>> operations, BucketPlan plan, TrieUpdaterMetrics? metrics, IRefCountingMemoryProvider? memoryProvider)
    {
        if (operations.IsEmpty) return currentRoot;
        memoryProvider ??= PooledRefCountingMemoryProvider.Instance;
        GroupFrameReader<TKey, TPath> reader = new(store, RootPath, metrics);
        try
        {
            using PbtNodeGroupWriter<TPath> writer = new(RootPath, memoryProvider);
            Subtree root = reader.Take(writer, PbtFourLevelGroupGeometry.RootPosition, allowAbsent: true);
            Subtree result = default;
            try
            {
                result = FoldMutations(store, metrics, ref reader, writer, memoryProvider, ref root, operations, 0, plan);
                ValueHash256 hash = writer.Write(PbtFourLevelGroupGeometry.RootPosition, 0, ref result);
                using (RefCountingMemory? payload = writer.Detach())
                    store.SetNodeGroup(reader.GroupKey, payload);
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

    /// <summary>Consumes a subtree and applies its mutation range, returning the canonical replacement.</summary>
    /// <remarks>
    /// Mutations sharing a prefix share traversal through four-bit groups (16 boundary slots).
    /// Shared prefixes skip intermediate groups; boundary folding recursively updates touched slots and recomposes them.
    /// </remarks>
    /// <param name="bitDepth">
    /// Absolute bit offset from the start of the key at which this call partitions the next nibble,
    /// aligned to a four-bit group. The keys' already shared prefix may extend beyond this offset.
    /// This may be deeper than the owner group after skipping a shared prefix.
    /// </param>
    private static Subtree FoldMutations(IPbtStore store, TrieUpdaterMetrics? metrics, ref GroupFrameReader<TKey, TPath> ownerReader, PbtNodeGroupWriter<TPath> ownerWriter, IRefCountingMemoryProvider memoryProvider,
        ref Subtree input, Span<PbtWriteOperation<TKey>> operations, int bitDepth, BucketPlan plan)
    {
        // Normally the subtree in a boundary slot of the parent group, whose reader/writer are passed here.
        // The initial call supplies the tree root; prefix jumps carry the same subtree to a deeper bitDepth.
        // A using local cannot be passed by ref. Keep current mutable so Move can clear it
        // when transferring ownership; finally disposes only the subtree still owned by this frame.
        Subtree current = Subtree.Move(ref input);
        try
        {
            if (operations.IsEmpty) return Subtree.Move(ref current);

            // At an empty subtree or leaf, a single update needs no partition unless it inserts a different key beside the leaf.
            // A default value denotes deletion, including a no-op when the key is absent.
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
                        return operation.Value == default ? default : new Subtree(operation);
                    if (operation.Value == default) return Subtree.Move(ref current);
                }
            }

            // Keys are logically variable-length despite each full-key type using a fixed-size inline buffer;
            // Length/BitLength identify the actual end, not the buffer capacity. Groups advance by a nibble,
            // but complete keys end on byte boundaries. A key ending here has no next nibble to bucket by.
            // Fold it separately from longer keys: deleting 0xAB and inserting 0xABCD must be allowed,
            // while keeping both would violate EIP-8297 prefix freedom.
            if (!TKey.IsFixedLength && bitDepth > 0 && (bitDepth & 7) == 0)
            {
                // Variable-length keys incur an extra linear scan here; fixed-length keys skip this cost.
                int terminalIndex = -1;
                for (int index = 0; index < operations.Length; index++)
                {
                    if (operations[index].Key.BitLength != bitDepth) continue;
                    terminalIndex = index;
                    break;
                }
                bool hasTerminalLeaf = current.IsLeaf && current.Key.BitLength == bitDepth;
                if (terminalIndex >= 0)
                {
                    // EIP-8297 prefix freedom applies to surviving keys, after both buckets have been folded.
                    Subtree terminal = hasTerminalLeaf ? Subtree.Move(ref current) : default;
                    Subtree descendants = default;
                    try
                    {
                        PbtWriteOperation<TKey> operation = operations[terminalIndex];
                        operations[..terminalIndex].CopyTo(operations[1..]);
                        operations[0] = operation;
                        terminal = FoldMutations(store, metrics, ref ownerReader, ownerWriter, memoryProvider, ref terminal, operations[..1], bitDepth, plan);
                        operations = operations[1..];
                        plan = plan.AfterFiltering(preservesOrder: true);
                        descendants = FoldMutations(store, metrics, ref ownerReader, ownerWriter, memoryProvider, ref current, operations, bitDepth, plan);
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
                if (hasTerminalLeaf)
                {
                    Subtree descendants = default;
                    try
                    {
                        descendants = FoldMutations(store, metrics, ref ownerReader, ownerWriter, memoryProvider, ref descendants, operations, bitDepth, plan);
                        if (!descendants.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
                        return Subtree.Move(ref current);
                    }
                    finally { descendants.Dispose(); }
                }
            }

            Span<byte> buffer = stackalloc byte[plan.GetBufferSize(operations.Length, bitDepth)];
            PartitionOutcome partition = plan.WithBuffer(buffer).BucketSort(operations, bitDepth, metrics);
            // The existing subtree may diverge before the mutations do. Stop at the four-bit group containing
            // that divergence rather than jumping solely by the mutations' shared prefix.
            TKey firstKey = operations[0].Key;
            int branchDepth = partition.Plan.KnownCommonPrefixLength;
            if (!current.IsEmpty && current.IsLeaf)
            {
                TKey leafKey = current.Key;
                int difference = leafKey.FirstDifferingBit(firstKey, bitDepth);
                branchDepth = Math.Min(branchDepth, difference);
            }
            else if (!current.IsEmpty)
            {
                int prefixStart = current.Path!.Value.BitDepth;
                branchDepth = Math.Min(branchDepth, prefixStart + MatchingPrefixBits(current.Prefix, firstKey, prefixStart));
            }
            // Integer floor to the preceding or equal group boundary.
            int groupDepth = branchDepth / PbtFourLevelGroupGeometry.LevelsPerGroup * PbtFourLevelGroupGeometry.LevelsPerGroup;
            // This is the shared-prefix path: both the mutations and existing subtree fit below one slot
            // of this group, so skip ahead. If branching occurs within this group, groupDepth == bitDepth
            // even when branchDepth is a few bits deeper; fold the current group below instead.
            if (groupDepth > bitDepth)
            {
                // This can skip multiple four-bit groups at once, e.g. bitDepth 8 to groupDepth 24.
                // The range's prefix survives the jump; the existing subtree only limits how far we can jump.
                return FoldMutations(store, metrics, ref ownerReader, ownerWriter, memoryProvider, ref current, operations, groupDepth, partition.Plan.ForChild());
            }

            // True when the requested group is already open (e.g. the root call at bitDepth 0): reuse its frame.
            // A child call advances bitDepth by four but receives the parent's reader, since its boundary node
            // is stored in that parent group. Then this is false, as it is after a deeper prefix jump;
            // open the descendant group below. The code that opened each frame is responsible for flushing it.
            if (ownerReader.BitDepth == bitDepth)
                return FoldBoundaryFromPartition(store, metrics, ref ownerReader, ownerWriter, memoryProvider, ref current, operations, bitDepth, partition);

            // A deeper group needs its own frame. Publish its completed contents here; the returned subtree root
            // is left for the caller to place, allowing composition to promote it through a compressed path.
            GroupFrameReader<TKey, TPath> reader = new(store, PbtNodePathOperations.FromKey<TPath>(operations[0].Key.Bytes, bitDepth), metrics);
            try
            {
                using PbtNodeGroupWriter<TPath> writer = new(reader.GroupKey, memoryProvider);
                Subtree result = FoldBoundaryFromPartition(store, metrics, ref reader, writer, memoryProvider, ref current, operations, bitDepth, partition);
                try
                {
                    using (RefCountingMemory? payload = writer.Detach())
                        store.SetNodeGroup(reader.GroupKey, payload);
                    return Subtree.Move(ref result);
                }
                finally { result.Dispose(); }
            }
            finally { reader.Dispose(); }
        }
        finally { current.Dispose(); }
    }

    private static Subtree FoldBoundaryFromPartition(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        ref GroupFrameReader<TKey, TPath> reader,
        PbtNodeGroupWriter<TPath> writer,
        IRefCountingMemoryProvider memoryProvider,
        ref Subtree current,
        Span<PbtWriteOperation<TKey>> operations,
        int bitDepth,
        PartitionOutcome partition)
    {
        RefList16<DecompositionEntry> boundaryBuffer = new(PbtFourLevelGroupGeometry.BoundarySlots);
        Span<DecompositionEntry> boundaries = boundaryBuffer.AsSpan();
        uint frontierMask = 0;
        try
        {
            Decompose(ref reader, writer, ref current, bitDepth, boundaries, ref frontierMask, partition.UsedMask);

            int offset = 0;
            int countIndex = 0;
            for (int mask = partition.UsedMask; mask != 0; mask &= mask - 1)
            {
                int slot = BitOperations.TrailingZeroCount(mask);
                int count = partition.Counts[countIndex++];
                Span<PbtWriteOperation<TKey>> bucket = operations.Slice(offset, count);
                offset += count;
                Subtree boundary = TakeBoundary(ref reader, writer, boundaries, ref frontierMask, slot);
                Subtree result = default;
                try
                {
                    result = FoldMutations(store, metrics, ref reader, writer, memoryProvider, ref boundary,
                        bucket, bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup, partition.Plan.ForChild());
                    SetBoundary(boundaries, ref frontierMask, slot, ref result);
                }
                finally
                {
                    boundary.Dispose();
                    result.Dispose();
                }
            }

            return Compose(ref reader, writer, metrics, boundaries, frontierMask);
        }
        finally { Dispose(boundaries); }
    }

    internal static int BoundaryPosition(int slot) => 2 * slot - BitOperations.PopCount((uint)slot);

    internal static Subtree TakeBoundary(ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer,
        Span<DecompositionEntry> frontier, ref uint frontierMask, int slot)
    {
        uint bit = 1u << BoundaryPosition(slot);
        if ((frontierMask & bit) == 0) return default;
        Subtree result = frontier[slot].TakeSubtree(ref reader, writer);
        frontierMask &= ~bit;
        return Subtree.Move(ref result);
    }

    internal static void SetBoundary(Span<DecompositionEntry> frontier, ref uint frontierMask, int slot, ref Subtree result)
    {
        uint bit = 1u << BoundaryPosition(slot);
        frontierMask = result.IsEmpty ? frontierMask & ~bit : frontierMask | bit;
        frontier[slot] = new(ref result);
    }

    internal static Subtree Compose(ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, TrieUpdaterMetrics? metrics, Span<DecompositionEntry> frontier, uint frontierMask)
    {
        ComposeFrameBuffer frames = default;
        int frameCount = 1;
        Subtree result = default;
        try
        {
            while (frameCount != 0)
            {
                ref ComposeFrame frame = ref frames[frameCount - 1];
                int position = frame.Path.Position;
                int width = frame.Path.Width;
                if (frame.Stage == ComposeStage.LeftCompleted)
                {
                    // Establish right occupancy without decoding it, so the left root can be emitted first.
                    uint rightMask = ((1u << (width - 1)) - 1) << (position - width + 1);
                    if ((frontierMask & rightMask) == 0)
                    {
                        frameCount--;
                        continue;
                    }

                    bool promoteRight = result.IsEmpty;
                    if (!promoteRight)
                    {
                        frame.LeftHash = writer.Write(position - width, reader.BitDepth + frame.Path.Length + 1, ref result);
                        frame.Stage = ComposeStage.RightCompleted;
                    }
                    if (width == 2)
                    {
                        result = TakeBoundary(ref reader, writer, frontier, ref frontierMask, frame.Path.Slot + 1);
                        if (promoteRight) frameCount--;
                    }
                    else if (promoteRight)
                        frame = new(frame.Path.Right);
                    else
                        frames[frameCount++] = new(frame.Path.Right);
                    continue;
                }
                if (frame.Stage == ComposeStage.RightCompleted)
                {
                    ValueHash256 rightHash = writer.Write(position - 1, reader.BitDepth + frame.Path.Length + 1, ref result);
                    result = new Subtree(BoundaryPath(reader.GroupKey, frame.Path.Slot, frame.Path.Length), frame.LeftHash, rightHash);
                    frameCount--;
                    continue;
                }
                if ((frontierMask & (1u << position)) != 0)
                {
                    result = frontier[frame.Path.Slot].TakeSubtree(ref reader, writer);
                    frontierMask &= ~(1u << position);
                    // An internal frontier entry is an unchanged subtree reached from the original input.
                    // Copy descendants only: its root may still be promoted by an updated sibling's deletion.
                    if (!result.IsLeaf)
                    {
                        int copied = reader.CopyRange(writer, position - 2 * width + 2, position);
                        if (copied != 0) metrics?.AddBulkCopy(copied);
                    }
                    frameCount--;
                    continue;
                }

                frame.Stage = ComposeStage.LeftCompleted;
                if (width == 2)
                    result = TakeBoundary(ref reader, writer, frontier, ref frontierMask, frame.Path.Slot);
                else
                    frames[frameCount++] = new(frame.Path.Left);
            }
            return Subtree.Move(ref result);
        }
        finally { result.Dispose(); }
    }

    private enum ComposeStage : byte { Descend, LeftCompleted, RightCompleted }

    private struct ComposeFrame(NodeGroupPath path)
    {
        internal NodeGroupPath Path = path;
        internal ComposeStage Stage;
        internal ValueHash256 LeftHash;
    }

    [InlineArray(PbtFourLevelGroupGeometry.LevelsPerGroup)]
    private struct ComposeFrameBuffer
    {
        private ComposeFrame _element;
    }

    /// <summary>Consumes the input into touched boundary nodes and opaque untouched siblings.</summary>
    /// <remarks>
    /// Entries use their leftmost boundary slot; the mask retains their group positions. An opaque subtree
    /// and its descendants never coexist, so their slots cannot collide. Only boundary entries are mutated.
    /// </remarks>
    internal static void Decompose(ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, ref Subtree current,
        int bitDepth, Span<DecompositionEntry> frontier, ref uint frontierMask, int touchedMask)
    {
        if (current.IsEmpty) return;
        int boundaryDepth = bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
        if (current.IsLeaf)
        {
            SetBoundary(frontier, ref frontierMask, BoundarySlot(current.Key.Bytes, bitDepth), ref current);
            return;
        }

        int branchDepth = current.Path!.Value.BitDepth + current.Prefix.BitCount;
        int effectiveLevel = Math.Min(branchDepth, boundaryDepth) - bitDepth;
        int branchSlot = 0;
        for (int bit = bitDepth; bit < bitDepth + effectiveLevel; bit++)
            branchSlot = (branchSlot << 1) | PrefixBit(current, bit);
        branchSlot <<= 4 - effectiveLevel;
        int branchWidth = 16 >> effectiveLevel;
        if (branchDepth >= boundaryDepth || (touchedMask & (((1 << branchWidth) - 1) << branchSlot)) == 0)
        {
            int position = 2 * (branchSlot + branchWidth) - 2 - BitOperations.PopCount((uint)branchSlot);
            frontier[branchSlot] = new(ref current);
            frontierMask |= 1u << position;
            return;
        }

        ValueHash256 left = current.LeftHash;
        ValueHash256 right = current.RightHash;
        current.Dispose();
        DecomposeChild(ref reader, writer, left, branchSlot, effectiveLevel + 1, bitDepth, frontier, ref frontierMask, touchedMask);
        DecomposeChild(ref reader, writer, right, branchSlot + branchWidth / 2, effectiveLevel + 1, bitDepth, frontier, ref frontierMask, touchedMask);
    }

    private static void DecomposeChild(ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer,
        ValueHash256 hash, int slot, int level, int bitDepth, Span<DecompositionEntry> frontier, ref uint frontierMask, int touchedMask)
    {
        if (hash == default) return;
        int width = 16 >> level;
        int position = 2 * (slot + width) - 2 - BitOperations.PopCount((uint)slot);
        if (level == 4 || (touchedMask & (((1 << width) - 1) << slot)) == 0)
        {
            frontier[slot] = new(hash, position);
            frontierMask |= 1u << position;
            return;
        }

        Subtree subtree = reader.Take(writer, position);
        try { Decompose(ref reader, writer, ref subtree, bitDepth, frontier, ref frontierMask, touchedMask); }
        finally { subtree.Dispose(); }
    }

    /// <summary>Owns a materialized frontier node or retains a non-owning group position for deferred acquisition.</summary>
    internal struct DecompositionEntry : IDisposable
    {
        private Subtree _subtree;
        private readonly byte _sourcePositionPlusOne;
        internal readonly int SourcePosition => _sourcePositionPlusOne - 1;
        internal readonly bool IsEmpty => _sourcePositionPlusOne == 0 && _subtree.IsEmpty;

        internal DecompositionEntry(ValueHash256 hash, int position) => _sourcePositionPlusOne = hash == default ? (byte)0 : (byte)(position + 1);

        internal DecompositionEntry(ref Subtree subtree) => _subtree = Subtree.Move(ref subtree);

        internal Subtree TakeSubtree(ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer)
        {
            Subtree subtree = _sourcePositionPlusOne != 0 ? reader.Take(writer, SourcePosition) : Subtree.Move(ref _subtree);
            this = default;
            return subtree;
        }

        public void Dispose()
        {
            _subtree.Dispose();
            this = default;
        }
    }

    internal static void Dispose(Span<DecompositionEntry> entries)
    {
        foreach (ref DecompositionEntry entry in entries) entry.Dispose();
    }

    private static int PrefixBit(Subtree subtree, int bit) => bit < subtree.Path!.Value.BitDepth
        ? subtree.Path!.Value.GetBit(bit)
        : GetBit(subtree.Prefix.Bytes, bit - subtree.Path!.Value.BitDepth);

    private static TPath BoundaryPath(TPath groupKey, int slot, int level)
    {
        if (level == 0) return groupKey;
        return groupKey.AppendBits(slot >> (4 - level), level);
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

        internal Subtree(RefCountingMemory lease, ReadOnlyMemory<byte> encoding, TPath? path)
        {
            _lease = lease;
            _kind = NodeKind.Original;
            _encoding = encoding;
            Path = path;
        }

        internal Subtree(PbtWriteOperation<TKey> operation)
        {
            _kind = NodeKind.Leaf;
            _key = operation.Key;
            _valueOrLeft = operation.Value;
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
            where TSourcePath : struct, IPbtNodePath<TSourcePath>
        {
            TKey key = default;
            TPath? path = null;
            if (source.IsLeaf)
            {
                TSourceKey sourceKey = source.Key;
                key = TKey.Create(sourceKey.Bytes);
            }
            else if (source.Path is { } sourcePath)
            {
                path = sourcePath.ToPath<TPath>();
            }
            Subtree result = new(source._lease, source._kind, source._encoding, key,
                source._valueOrLeft, source._right, path);
            source = default;
            return result;
        }

        private readonly PbtNodeReader Reader => PbtNodeReader.FromValidated(_encoding.Span);
        internal readonly TPath? Path { get; }
        internal readonly bool IsEmpty => _kind == NodeKind.Empty;
        internal readonly bool IsLeaf => _kind == NodeKind.Leaf || (_kind == NodeKind.Original && Reader.IsLeaf);
        internal readonly TKey Key => _kind == NodeKind.Leaf ? _key : TKey.Create(Reader.Key);
        internal readonly CompressedPrefix Prefix => _kind == NodeKind.Branch ? default : Reader.Prefix;
        internal readonly ValueHash256 LeftHash => _kind == NodeKind.Branch ? _valueOrLeft : Reader.LeftHash;
        internal readonly ValueHash256 RightHash => _kind == NodeKind.Branch ? _right : Reader.RightHash;

        internal readonly int EncodedLength(int depth) => IsLeaf
            ? (_kind == NodeKind.Leaf ? 3 + _key.Length + 32 : _encoding.Length)
            : 3 + PbtBitPrefix.ByteCount(Path!.Value.BitDepth + Prefix.BitCount - depth) + 64;

        internal readonly ValueHash256 Encode(Span<byte> encoding, int depth)
        {
            if (_kind == NodeKind.Original && (IsLeaf || depth == Path!.Value.BitDepth))
            {
                _encoding.Span.CopyTo(encoding);
                return PbtNodeCodec.Hash(Reader);
            }
            if (IsLeaf)
            {
                PbtNodeCodec.EncodeLeaf(encoding, _key, _valueOrLeft.Bytes);
                return PbtNodeCodec.Hash(PbtNodeReader.FromValidated(encoding));
            }

            // EIP-8297 promotion absorbs skipped path bits; the source anchor remains unchanged until placement.
            int pathDepth = Path!.Value.BitDepth;
            int bitCount = pathDepth + Prefix.BitCount - depth;
            PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, LeftHash, RightHash);
            Span<byte> prefix = encoding.Slice(3, PbtBitPrefix.ByteCount(bitCount));
            int pathBits = Math.Max(0, pathDepth - depth);
            Path!.Value.CopyBitsTo(Math.Min(depth, pathDepth), prefix, 0, pathBits);
            int prefixOffset = Math.Max(0, depth - pathDepth);
            PbtBitPrefix.CopyBits(Prefix.Bytes, prefixOffset, bitCount - pathBits, prefix, pathBits);
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

    private static int MatchingPrefixBits(CompressedPrefix prefix, TKey key, int keyOffset)
    {
        ReadOnlySpan<byte> prefixBytes = prefix.Bytes;
        int available = key.BitLength - keyOffset;
        int count = Math.Min(prefix.BitCount, available);
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
}
