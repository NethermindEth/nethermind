// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
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

    /// <summary>Frames with fewer operations fold their buckets serially; narrower fan-out costs more than it saves.</summary>
    internal const int MinOperationsToFoldInParallel = 1024;

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

    // Path buffers are cleared by the PbtTraversalPath constructor and the bucket buffer is written by
    // BucketSort before it is read, so the descent frames skip zero-initialization.
    [SkipLocalsInit]
    private static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, Span<PbtWriteOperation<TKey>> operations, BucketPlan plan, TrieUpdaterMetrics? metrics, IRefCountingMemoryProvider? memoryProvider)
    {
        if (operations.IsEmpty) return currentRoot;
        FoldContext context = new(store, memoryProvider ?? PooledRefCountingMemoryProvider.Instance, metrics, null, null);
        Span<byte> pathBuffer = stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)];
        PbtTraversalPath path = new(pathBuffer);
        GroupFrameReader<TKey, TPath> reader = new(store, 0, currentRoot, metrics);
        using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
        {
            using PbtNodeGroupWriter<TPath> writer = new(0, context.MemoryProvider);
            TraversalSubtree root = new(path, reader.Take(path, writer, PbtFourLevelGroupGeometry.RootPosition, allowAbsent: true));
            OwnedSubtree result = FoldMutations(context, ref reader, writer, root, operations, ref path, 0, plan);
            TraversalSubtree resolved = result.Borrow(stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)]);
            ValueHash256 hash = writer.Write(path, PbtFourLevelGroupGeometry.RootPosition, 0, ref resolved, metrics);
            using (RefCountingMemory? payload = writer.Detach())
                store.SetNodeGroup(path, hash, payload);
            return hash;
        }
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
    [SkipLocalsInit]
    private static OwnedSubtree FoldMutations(FoldContext context, ref GroupFrameReader<TKey, TPath> ownerReader, PbtNodeGroupWriter<TPath> ownerWriter,
        scoped TraversalSubtree input, Span<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, scoped BucketPlan plan)
    {
        Debug.Assert(path.BitDepth == bitDepth);
        TrieUpdaterMetrics? metrics = context.Metrics;
        // Normally the subtree in a boundary slot of the parent group, whose reader/writer are passed here.
        // The initial call supplies the tree root; prefix jumps carry the same subtree to a deeper bitDepth.
        TraversalSubtree current = input;
        if (operations.IsEmpty) return current.Materialize();

        // At an empty subtree or leaf, a single update needs no partition unless it inserts a different key beside the leaf.
        // A default value denotes deletion, including a no-op when the key is absent.
        if (current.IsEmpty)
        {
            if (operations.Length == 1)
                return operations[0].Value == default ? default : new OwnedSubtree(default, new Subtree(operations[0]));
        }
        else if (current.IsLeaf)
        {
            if (operations.Length == 1)
            {
                PbtWriteOperation<TKey> operation = operations[0];
                if (operation.Key.Equals(current.Node.Key))
                {
                    if (operation.Value == default) return default;
                    return operation.Value == current.Node.Value ? current.Materialize() : new OwnedSubtree(default, new Subtree(operation));
                }
                if (operation.Value == default) return current.Materialize();
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
            bool hasTerminalLeaf = current.IsLeaf && current.Node.Key.BitLength == bitDepth;
            if (terminalIndex >= 0)
            {
                // EIP-8297 prefix freedom applies to surviving keys, after both buckets have been folded.
                TraversalSubtree terminal = hasTerminalLeaf ? TraversalSubtree.Move(ref current) : default;
                PbtWriteOperation<TKey> operation = operations[terminalIndex];
                operations[..terminalIndex].CopyTo(operations[1..]);
                operations[0] = operation;
                OwnedSubtree terminalResult = FoldMutations(context, ref ownerReader, ownerWriter, terminal, operations[..1], ref path, bitDepth, plan);
                operations = operations[1..];
                plan = plan.AfterFiltering(preservesOrder: true);
                OwnedSubtree descendantResult = FoldMutations(context, ref ownerReader, ownerWriter, current, operations, ref path, bitDepth, plan);
                if (terminalResult.IsEmpty) return descendantResult;
                if (!descendantResult.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
                return terminalResult;
            }
            if (hasTerminalLeaf)
            {
                TraversalSubtree descendants = default;
                OwnedSubtree descendantResult = FoldMutations(context, ref ownerReader, ownerWriter, descendants, operations, ref path, bitDepth, plan);
                if (!descendantResult.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
                return current.Materialize();
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
            TKey leafKey = current.Node.Key;
            int difference = leafKey.FirstDifferingBit(firstKey, bitDepth);
            branchDepth = Math.Min(branchDepth, difference);
        }
        else if (!current.IsEmpty)
        {
            branchDepth = Math.Min(branchDepth, current.FirstDifferingBit(firstKey, bitDepth));
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
            path.AppendKey(firstKey.Bytes, groupDepth);
            OwnedSubtree result = FoldMutations(context, ref ownerReader, ownerWriter, current, operations, ref path, groupDepth, partition.Plan.ForChild());
            path.Truncate(bitDepth);
            return result;
        }

        // True when the requested group is already open (e.g. the root call at bitDepth 0): reuse its frame.
        // A child call advances bitDepth by four but receives the parent's reader, since its boundary node
        // is stored in that parent group. Then this is false, as it is after a deeper prefix jump;
        // open the descendant group below. The code that opened each frame is responsible for flushing it.
        if (ownerReader.BitDepth == bitDepth)
            return FoldBoundaryFromPartition(context, ref ownerReader, ownerWriter, current, operations, ref path, bitDepth, partition);

        // A deeper group needs its own frame. Publish its completed contents here; the returned subtree root
        // is left for the caller to place, allowing composition to promote it through a compressed path.
        GroupFrameReader<TKey, TPath> reader = new(context.Store, bitDepth, current.Hash(bitDepth, metrics), metrics);
        using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
        {
            using PbtNodeGroupWriter<TPath> writer = new(bitDepth, context.MemoryProvider);
            OwnedSubtree result = FoldBoundaryFromPartition(context, ref reader, writer, current, operations, ref path, bitDepth, partition);
            using (RefCountingMemory? payload = writer.Detach())
                context.Store.SetNodeGroup(path, result.Borrow(stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)]).Hash(bitDepth, metrics), payload);
            return result;
        }
    }

    [SkipLocalsInit]
    private static OwnedSubtree FoldBoundaryFromPartition(
        FoldContext context,
        ref GroupFrameReader<TKey, TPath> reader,
        PbtNodeGroupWriter<TPath> writer,
        scoped TraversalSubtree current,
        Span<PbtWriteOperation<TKey>> operations,
        ref PbtTraversalPath path,
        int bitDepth,
        scoped PartitionOutcome partition)
    {
        Debug.Assert(path.BitDepth == bitDepth);
        Frontier frontier = default;
        Decompose(ref reader, writer, path, ref current, bitDepth, ref frontier, partition.UsedMask);
        Span<byte> sourceBuffer = stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)];

        if (context.BucketFoldOptions is { } foldOptions && operations.Length >= MinOperationsToFoldInParallel && !BitOperations.IsPow2((uint)partition.UsedMask))
            FoldBucketsInParallel(context, foldOptions, ref reader, writer, ref frontier, operations, path, bitDepth, partition, sourceBuffer);
        else
            FoldBuckets(context, ref reader, writer, ref frontier, operations, ref path, bitDepth, partition, sourceBuffer);

        return Compose(ref reader, writer, path, context.Metrics, ref frontier, sourceBuffer).Materialize();
    }

    private static void FoldBuckets(FoldContext context, scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer,
        scoped ref Frontier frontier, Span<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, scoped PartitionOutcome partition, scoped Span<byte> sourceBuffer)
    {
        int offset = 0;
        int countIndex = 0;
        for (int mask = partition.UsedMask; mask != 0; mask &= mask - 1)
        {
            int slot = BitOperations.TrailingZeroCount(mask);
            int count = partition.Counts[countIndex++];
            Span<PbtWriteOperation<TKey>> bucket = operations.Slice(offset, count);
            offset += count;
            TraversalSubtree boundary = TakeBoundary(ref reader, writer, path, ref frontier, slot, sourceBuffer);
            path.AppendMut(slot);
            OwnedSubtree result = FoldMutations(context, ref reader, writer, boundary,
                bucket, ref path, bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup, partition.Plan.ForChild());
            path.Truncate(bitDepth);
            TraversalSubtree resolved = result.Borrow(sourceBuffer);
            SetBoundary(path, ref frontier, slot, ref resolved);
        }
    }

    /// <summary>Folds each touched bucket on its own thread, taking the boundaries before and placing the results after.</summary>
    /// <remarks>
    /// Every child opens and publishes its own group, so the parent frame is only read here and each group
    /// keeps a single writer. Results are materialized copies, so no reader lease crosses threads.
    /// </remarks>
    private static void FoldBucketsInParallel(FoldContext context, ParallelOptions foldOptions, scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer,
        scoped ref Frontier frontier, Span<PbtWriteOperation<TKey>> operations, scoped PbtTraversalPath path, int bitDepth, scoped PartitionOutcome partition, scoped Span<byte> sourceBuffer)
    {
        using ArrayPoolList<BucketFold> buckets = new(BitOperations.PopCount((uint)partition.UsedMask));
        int offset = OffsetOf(context.Operations!, operations);
        int countIndex = 0;
        for (int mask = partition.UsedMask; mask != 0; mask &= mask - 1)
        {
            int slot = BitOperations.TrailingZeroCount(mask);
            int count = partition.Counts[countIndex++];
            OwnedSubtree boundary = TakeBoundary(ref reader, writer, path, ref frontier, slot, sourceBuffer).Materialize();
            buckets.Add(new BucketFold(slot, offset, count, boundary, context.Metrics is null ? context : context.WithMetrics(new())));
            offset += count;
        }

        TPath groupPath = path.ToPath<TPath>();
        int knownCommonPrefixLength = partition.Plan.KnownCommonPrefixLength;
        bool isSorted = partition.Plan.IsSorted;
        ParallelUnbalancedWork.For(0, buckets.Count, foldOptions, index => buckets[index].Fold(groupPath, bitDepth, knownCommonPrefixLength, isSorted));

        foreach (BucketFold bucket in buckets)
        {
            if (bucket.Context.Metrics is { } bucketMetrics) context.Metrics!.Add(bucketMetrics);
            TraversalSubtree resolved = bucket.Result.Borrow(sourceBuffer);
            SetBoundary(path, ref frontier, bucket.Slot, ref resolved);
        }
    }

    private static int OffsetOf(PbtWriteOperation<TKey>[] array, Span<PbtWriteOperation<TKey>> span)
    {
        int offset = (int)(Unsafe.ByteOffset(ref MemoryMarshal.GetArrayDataReference(array), ref MemoryMarshal.GetReference(span)) / Unsafe.SizeOf<PbtWriteOperation<TKey>>());
        Debug.Assert(offset >= 0 && offset + span.Length <= array.Length, "The operation range must lie within the batch array.");
        return offset;
    }

    /// <summary>Per-fold state shared by every frame of one root update.</summary>
    /// <remarks>
    /// <see cref="BucketFoldOptions"/> and <see cref="Operations"/> are set only when wide frames fold their buckets
    /// concurrently; the latter is the backing array of every operation range, so a bucket can rebuild its span on another thread.
    /// <see cref="Metrics"/> is not thread-safe, so each concurrent bucket folds under <see cref="WithMetrics"/> and is merged afterwards.
    /// </remarks>
    internal sealed class FoldContext(IPbtStore store, IRefCountingMemoryProvider memoryProvider, TrieUpdaterMetrics? metrics, ParallelOptions? bucketFoldOptions, PbtWriteOperation<TKey>[]? operations)
    {
        internal IPbtStore Store { get; } = store;
        internal IRefCountingMemoryProvider MemoryProvider { get; } = memoryProvider;
        internal TrieUpdaterMetrics? Metrics { get; } = metrics;
        internal ParallelOptions? BucketFoldOptions { get; } = bucketFoldOptions;
        internal PbtWriteOperation<TKey>[]? Operations { get; } = operations;

        internal FoldContext WithMetrics(TrieUpdaterMetrics metrics) => new(Store, MemoryProvider, metrics, BucketFoldOptions, Operations);
    }

    private sealed class BucketFold(int slot, int offset, int count, OwnedSubtree current, FoldContext context)
    {
        internal int Slot { get; } = slot;
        internal FoldContext Context { get; } = context;
        internal OwnedSubtree Result;

        [SkipLocalsInit]
        internal void Fold(TPath groupPath, int bitDepth, int knownCommonPrefixLength, bool isSorted)
        {
            Span<byte> pathBuffer = stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)];
            PbtTraversalPath path = PbtTraversalPath.FromPath(pathBuffer, groupPath);
            path.AppendMut(Slot);
            // A child below the boundary only compares the owner frame's depth with its own, so an unloaded stand-in
            // replaces the parent's frame, which must not be shared across threads.
            GroupFrameReader<TKey, TPath> owner = new(Context.Store, bitDepth, default, null);
            using PbtNodeGroupWriter<TPath> ownerWriter = new(bitDepth, Context.MemoryProvider);
            TraversalSubtree boundary = current.Borrow(stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)]);
            Result = FoldMutations(Context, ref owner, ownerWriter, boundary, Context.Operations!.AsSpan(offset, count),
                ref path, bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup, new BucketPlan(default, knownCommonPrefixLength, isSorted));
        }
    }

    internal static int BoundaryPosition(int slot) => 2 * slot - BitOperations.PopCount((uint)slot);

    internal static TraversalSubtree TakeBoundary(scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path,
        scoped ref Frontier frontier, int slot, Span<byte> sourceBuffer)
    {
        uint bit = 1u << BoundaryPosition(slot);
        if ((frontier.Mask & bit) == 0) return default;
        TraversalSubtree result = frontier.Take(ref reader, writer, path, slot, sourceBuffer);
        frontier.Mask &= ~bit;
        return result;
    }

    internal static void SetBoundary(PbtTraversalPath path, ref Frontier frontier, int slot, ref TraversalSubtree result)
    {
        uint bit = 1u << BoundaryPosition(slot);
        frontier.Mask = result.IsEmpty ? frontier.Mask & ~bit : frontier.Mask | bit;
        frontier.Set(path, slot, ref result);
    }

    [SkipLocalsInit]
    internal static TraversalSubtree Compose(scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path, TrieUpdaterMetrics? metrics, scoped ref Frontier frontier, Span<byte> sourceBuffer)
    {
        ComposeFrameBuffer frames = default;
        int frameCount = 1;
        TraversalSubtree result = default;
        while (frameCount != 0)
        {
            ref ComposeFrame frame = ref frames[frameCount - 1];
            int position = frame.Path.Position;
            int width = frame.Path.Width;
            if (frame.Stage == ComposeStage.LeftCompleted)
            {
                // Establish right occupancy without decoding it, so the left root can be emitted first.
                uint rightMask = ((1u << (width - 1)) - 1) << (position - width + 1);
                if ((frontier.Mask & rightMask) == 0)
                {
                    frameCount--;
                    continue;
                }

                bool promoteRight = result.IsEmpty;
                if (!promoteRight)
                {
                    frame.LeftHash = writer.Write(path, position - width, reader.BitDepth + frame.Path.Length + 1, ref result, metrics);
                    frame.Stage = ComposeStage.RightCompleted;
                }
                if (width == 2)
                {
                    result = TakeBoundary(ref reader, writer, path, ref frontier, frame.Path.Slot + 1, sourceBuffer);
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
                ValueHash256 rightHash = writer.Write(path, position - 1, reader.BitDepth + frame.Path.Length + 1, ref result, metrics);
                result = new TraversalSubtree(path, new Subtree(frame.Path, frame.LeftHash, rightHash));
                frameCount--;
                continue;
            }
            if ((frontier.Mask & (1u << position)) != 0)
            {
                result = frontier.Take(ref reader, writer, path, frame.Path.Slot, sourceBuffer);
                frontier.Mask &= ~(1u << position);
                // An internal frontier entry is an unchanged subtree reached from the original input.
                // Copy descendants only: its root may still be promoted by an updated sibling's deletion.
                if (!result.IsLeaf)
                {
                    int copied = reader.CopyRange(path, writer, position - 2 * width + 2, position);
                    if (copied != 0) metrics?.AddBulkCopy(copied);
                }
                frameCount--;
                continue;
            }

            frame.Stage = ComposeStage.LeftCompleted;
            if (width == 2)
                result = TakeBoundary(ref reader, writer, path, ref frontier, frame.Path.Slot, sourceBuffer);
            else
                frames[frameCount++] = new(frame.Path.Left);
        }
        return result;
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
    [SkipLocalsInit]
    internal static void Decompose(ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path, ref TraversalSubtree current,
        int bitDepth, ref Frontier frontier, int touchedMask)
    {
        if (current.IsEmpty) return;
        int boundaryDepth = bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
        if (current.IsLeaf)
        {
            SetBoundary(path, ref frontier, BoundarySlot(current.Node.Key.Bytes, bitDepth), ref current);
            return;
        }

        int branchDepth = current.AnchorDepth + current.Node.Prefix.BitCount;
        int effectiveLevel = Math.Min(branchDepth, boundaryDepth) - bitDepth;
        int branchSlot = 0;
        for (int bit = bitDepth; bit < bitDepth + effectiveLevel; bit++)
            branchSlot = (branchSlot << 1) | current.PrefixBit(bit);
        branchSlot <<= 4 - effectiveLevel;
        int branchWidth = 16 >> effectiveLevel;
        if (branchDepth >= boundaryDepth || (touchedMask & (((1 << branchWidth) - 1) << branchSlot)) == 0)
        {
            int position = 2 * (branchSlot + branchWidth) - 2 - BitOperations.PopCount((uint)branchSlot);
            frontier.Set(path, branchSlot, ref current);
            frontier.Mask |= 1u << position;
            return;
        }

        ValueHash256 left = current.Node.LeftHash;
        ValueHash256 right = current.Node.RightHash;
        current = default;
        DecomposeChild(ref reader, writer, path, left, branchSlot, effectiveLevel + 1, bitDepth, ref frontier, touchedMask);
        DecomposeChild(ref reader, writer, path, right, branchSlot + branchWidth / 2, effectiveLevel + 1, bitDepth, ref frontier, touchedMask);
    }

    private static void DecomposeChild(ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path,
        ValueHash256 hash, int slot, int level, int bitDepth, ref Frontier frontier, int touchedMask)
    {
        if (hash == default) return;
        int width = 16 >> level;
        int position = 2 * (slot + width) - 2 - BitOperations.PopCount((uint)slot);
        reader.SeedHash(position, hash);
        if (level == 4 || (touchedMask & (((1 << width) - 1) << slot)) == 0)
        {
            frontier.Entries[slot] = new(hash, position);
            frontier.Mask |= 1u << position;
            return;
        }

        TraversalSubtree subtree = new(path, reader.Take(path, writer, position));
        Decompose(ref reader, writer, path, ref subtree, bitDepth, ref frontier, touchedMask);
    }

    internal static int MatchingPrefixBits(CompressedPrefix prefix, TKey key, int keyOffset)
    {
        if (prefix.BitCount == 0) return 0;

        ReadOnlySpan<byte> prefixBytes = prefix.Bytes;
        int available = key.BitLength - keyOffset;
        int count = Math.Min(prefix.BitCount, available);
        ReadOnlySpan<byte> keyBytes = key.Bytes;
        int keyBitOffset = keyOffset & 7;
        if (keyBitOffset == 0)
            return Math.Min(count, PbtKeyOperations.FirstDifferingBit(prefixBytes, keyBytes[(keyOffset >> 3)..], 0));

        int index = 0;
        for (; index + 64 <= count; index += 64)
        {
            int keyByteIndex = (keyOffset + index) >> 3;
            ulong keyWord = (BinaryPrimitives.ReadUInt64BigEndian(keyBytes[keyByteIndex..]) << keyBitOffset)
                | (uint)(keyBytes[keyByteIndex + sizeof(ulong)] >> (8 - keyBitOffset));
            ulong difference = BinaryPrimitives.ReadUInt64BigEndian(prefixBytes[(index >> 3)..]) ^ keyWord;
            if (difference != 0) return index + BitOperations.LeadingZeroCount(difference);
        }
        for (; index + 8 <= count; index += 8)
        {
            int keyByteIndex = (keyOffset + index) >> 3;
            int keyByte = ((keyBytes[keyByteIndex] << 8) | keyBytes[keyByteIndex + 1]) >> (8 - keyBitOffset);
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
    /// <summary>Node hashes computed from an encoding, excluding those reused from a parent node.</summary>
    internal int NodeHashes { get; private set; }

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
        NodeHashes += metrics.NodeHashes;
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
    internal void IncrementNodeHashes() => NodeHashes++;
}
