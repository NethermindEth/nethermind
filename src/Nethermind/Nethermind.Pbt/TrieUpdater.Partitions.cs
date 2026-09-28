// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Attributes;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Core.Threading;
using static Nethermind.Pbt.TrieUpdater<Nethermind.Pbt.PbtStorageTreeKey, Nethermind.Pbt.PbtStorageNodePath>;

namespace Nethermind.Pbt;

public static partial class TrieUpdater
{
    private static readonly StringLabel _accountFoldLabel = new("account");
    private static readonly StringLabel _codeFoldLabel = new("code");
    private static readonly StringLabel _storageFoldLabel = new("storage");

    /// <summary>Folds disjoint partitions concurrently before merging their shared ancestors.</summary>
    /// <remarks>
    /// The zones, their shards' sorts and the touched slots of every frame wide enough to fan out, merged into runs of at least the
    /// minimum <paramref name="fanOut"/> gives for the frame's subtree size, share <paramref name="foldQuota"/>: a fan-out folds its
    /// parts on the calling thread while no slot is free, and the first slot it takes admits a parallel loop over
    /// the parts still left, whose workers charge themselves as they start; a quota of one folds everything
    /// serially. Each zone's fold time is observed on <paramref name="partitionFoldTime"/> labelled by partition, so
    /// an imbalance between them is visible.
    /// Each zone's shards are sorted in place, and with four-level groups the producer's shard counts serve as the zone group's slot ranges.
    /// The supplied store must support concurrent reads; each worker writes through its own <see cref="IPbtStore.CreateWriter"/>. Failed
    /// folds may leave partial writes; the caller owns failure isolation and must not reuse that state without
    /// recovery.
    /// </remarks>
    [SkipLocalsInit]
    internal static ValueHash256 UpdateRoot(
        IPbtStore store,
        in ValueHash256 currentRoot,
        PbtPartitionBatches changes,
        ConcurrencyController foldQuota,
        FoldFanOut fanOut,
        IMetricObserver? partitionFoldTime,
        IRefCountingMemoryProvider? memoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(foldQuota);
        using ArrayPoolList<PartitionFold> workers = new(3);
        memoryProvider ??= PooledRefCountingMemoryProvider.Instance;
        try
        {
            AddWorker<PbtPath, PbtNodePath>(changes.Account, Eip8297KeyDerivation.AccountZone, _accountFoldLabel);
            AddWorker<PbtPath, PbtNodePath>(changes.Code, Eip8297KeyDerivation.CodeZone, _codeFoldLabel);
            AddWorker<PbtStoragePath, PbtStorageNodePath>(changes.Storage, Eip8297KeyDerivation.StorageZone, _storageFoldLabel);
            if (workers.Count == 0) return currentRoot;

            using IPbtConcurrentWriter storeWriter = store.CreateWriter();
            PbtTraversalPath rootPath = new(Span<byte>.Empty);
            if (!GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.TryLoad(store, rootPath, currentRoot,
                    out GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> rootReader))
            {
                AbsentGroupFrame<PbtStorageTreeKey, PbtStorageNodePath> emptyRoot = new(0);
                return FoldZones(store, ref emptyRoot, default, workers, storeWriter, foldQuota, memoryProvider);
            }
            using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref rootReader))
                return FoldZones(store, ref rootReader, rootReader.TakeRoot(), workers, storeWriter, foldQuota, memoryProvider);
        }
        finally
        {
            foreach (PartitionFold worker in workers) worker.Dispose();
        }

        void AddWorker<TKey, TPath>(PbtWriteBatch<TKey>? batch, byte zone, StringLabel foldLabel)
            where TKey : unmanaged, IPbtKey<TKey>
            where TPath : struct, IPbtNodePath<TPath>
        {
            if (batch is null) return;
            ArgumentOutOfRangeException.ThrowIfNotEqual(batch.ShardNibbleIndex, 2);
            batch.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table);
            PartitionFold<TKey, TPath> worker = new(store, zone, operations, table, memoryProvider, foldQuota, fanOut,
                partitionFoldTime, foldLabel);
            if (operations.Count != 0) workers.Add(worker);
            else worker.Dispose();
        }
    }

    /// <summary>Folds the workers' units below the groups they share with each other, then composes and publishes those shared groups.</summary>
    /// <remarks>
    /// The shared groups are the ones above <see cref="PbtGroupGeometry.ZoneGroupDepth"/>, which the zones' units are the
    /// boundary slots of the deepest of. They are kept in level order, each level's groups in path order, so the children
    /// of one level's groups, taken slot by slot, are the next level's groups, or the units below the last level. Every
    /// shared frame is opened and every unit's boundary node taken on the calling thread before the workers start, and
    /// the frames stay open until the workers' results are composed back up through them.
    /// </remarks>
    [SkipLocalsInit]
    private static ValueHash256 FoldZones<TRoot>(IPbtStore store, ref TRoot rootReader, BoundaryNode root, ArrayPoolList<PartitionFold> workers,
        IPbtConcurrentWriter storeWriter, ConcurrencyController foldQuota, IRefCountingMemoryProvider memoryProvider)
        where TRoot : struct, IGroupFrame<PbtStorageTreeKey, PbtStorageNodePath>
    {
        int unitCount = 0;
        foreach (PartitionFold worker in workers) unitCount += worker.Units.Length;
        Span<int> unitPaths = stackalloc int[unitCount];
        unitCount = 0;
        foreach (PartitionFold worker in workers)
            foreach (ref PartitionFold.Unit unit in worker.Units.AsSpan())
                unitPaths[unitCount++] = worker.Zone << PbtGroupGeometry.UnitBits | unit.Bits;

        int levels = PbtGroupGeometry.ZoneGroupDepth / PbtGroupGeometry.LevelsPerGroup;
        int groupCount = 0;
        for (int level = 0; level < levels; level++) groupCount += CountGroups(unitPaths, level);
        SharedGroup[] groups = ArrayPool<SharedGroup>.Shared.Rent(groupCount);
        FoldResult[] results = ArrayPool<FoldResult>.Shared.Rent(groupCount * PbtGroupGeometry.BoundarySlots);
        try
        {
            int resultCount = 0;
            groupCount = 0;
            for (int level = 0; level < levels; level++)
            {
                int depth = level * PbtGroupGeometry.LevelsPerGroup;
                foreach (int unitPath in unitPaths)
                {
                    int pathBits = unitPath >> (PbtGroupGeometry.ZoneGroupDepth - depth);
                    if (groupCount == 0 || groups[groupCount - 1].Depth != depth || groups[groupCount - 1].PathBits != pathBits)
                    {
                        if (groupCount != 0) resultCount += groups[groupCount - 1].Touched.PopCount();
                        groups[groupCount++] = new SharedGroup { Depth = depth, PathBits = pathBits, ResultOffset = resultCount };
                    }
                    groups[groupCount - 1].Touched.Set((unitPath >> (PbtGroupGeometry.ZoneGroupDepth - depth - PbtGroupGeometry.LevelsPerGroup)) & (PbtGroupGeometry.BoundarySlots - 1));
                }
            }

            Span<byte> pathBuffer = stackalloc byte[1];
            ref SharedGroup rootGroup = ref groups[0];
            rootGroup.Writer = PbtNodeGroupWriter<PbtStorageNodePath>.Rent(0, memoryProvider);
            rootGroup.Frontier = new(rootGroup.Touched);
            PbtTraversalPath rootPath = new(pathBuffer[..0]);
            Decompose(ref rootReader, rootPath, ref root, 0, ref rootGroup.Frontier, rootGroup.Touched);
            int nextChild = 1;
            int nextUnit = 0;
            DescendGroup(store, ref rootReader, ref rootGroup, groups, ref nextChild, workers, ref nextUnit, memoryProvider);
            for (int index = 1; index < groupCount; index++)
            {
                ref SharedGroup group = ref groups[index];
                if (group.IsAbsent) DescendGroup(store, ref group.Absent, ref group, groups, ref nextChild, workers, ref nextUnit, memoryProvider);
                else DescendGroup(store, ref group.Reader, ref group, groups, ref nextChild, workers, ref nextUnit, memoryProvider);
            }

            FoldWorkers(workers, foldQuota);

            // Deeper groups are composed first, so each group's children hold their results when it is.
            nextUnit = unitCount;
            nextChild = groupCount;
            for (int index = groupCount - 1; index >= 0; index--)
            {
                ref SharedGroup group = ref groups[index];
                Span<FoldResult> groupResults = results.AsSpan(group.ResultOffset, group.Touched.PopCount());
                bool childrenAreUnits = group.Depth + PbtGroupGeometry.LevelsPerGroup == PbtGroupGeometry.ZoneGroupDepth;
                int childCount = group.Touched.PopCount();
                int child = childrenAreUnits ? nextUnit -= childCount : nextChild -= childCount;
                for (int slot = group.Touched.NextSetBit(0); slot >= 0; slot = group.Touched.NextSetBit(slot + 1))
                {
                    ref FoldResult childResult = ref childrenAreUnits ? ref UnitAt(workers, child++).Result : ref groups[child++].Result;
                    group.Writer!.AddDescendantDelta(slot, childResult.SizeDelta);
                    SetBoundary(ref group.Frontier, groupResults, slot, ref childResult);
                }
                if (index == 0) break;
                PbtTraversalPath groupPath = SharedPath(pathBuffer, group.Depth, group.PathBits);
                if (group.IsAbsent) ComposeShared(ref group.Absent, ref group, groupPath, groupResults, storeWriter);
                else ComposeShared(ref group.Reader, ref group, groupPath, groupResults, storeWriter);
            }

            FoldResult rootResult = default;
            Compose(ref rootReader, ref rootGroup.Hashes, rootGroup.Writer!, rootPath, 0, ref rootGroup.Frontier, results.AsSpan(0, rootGroup.Touched.PopCount()), ref rootResult);
            ValueHash256 hash = rootGroup.Writer!.WriteRoot(rootPath, rootResult);
            PublishGroup(storeWriter, ref rootReader, rootGroup.Writer, rootPath, hash);
            return hash;
        }
        finally
        {
            foreach (ref SharedGroup group in groups.AsSpan(0, groupCount))
            {
                group.Reader.Dispose();
                group.Writer?.Dispose();
                group.Hashes.Dispose();
            }
            ArrayPool<SharedGroup>.Shared.Return(groups, clearArray: true);
            ArrayPool<FoldResult>.Shared.Return(results, clearArray: true);
        }

        static int CountGroups(ReadOnlySpan<int> unitPaths, int level)
        {
            int shift = PbtGroupGeometry.ZoneGroupDepth - level * PbtGroupGeometry.LevelsPerGroup;
            int count = 0;
            for (int index = 0; index < unitPaths.Length; index++)
                if (index == 0 || unitPaths[index] >> shift != unitPaths[index - 1] >> shift) count++;
            return count;
        }
    }

    /// <summary>The path of the shared group at <paramref name="depth"/>, whose bits all lie in the zone byte.</summary>
    private static PbtTraversalPath SharedPath(Span<byte> buffer, int depth, int pathBits)
    {
        PbtTraversalPath path = new(buffer);
        path.AppendBits(pathBits, depth);
        return path;
    }

    private static ref PartitionFold.Unit UnitAt(ArrayPoolList<PartitionFold> workers, int index)
    {
        foreach (PartitionFold worker in workers)
        {
            if (index < worker.Units.Length) return ref worker.Units[index];
            index -= worker.Units.Length;
        }
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    /// <summary>Takes the boundary node of every touched slot of <paramref name="group"/>, opening the shared group below it or handing it to its unit.</summary>
    /// <remarks>Workers fold on other threads, so a unit's slot size is handed over here too, to the only boundary that inherits it.</remarks>
    private static void DescendGroup<TFrame>(IPbtStore store, ref TFrame frame, ref SharedGroup group, SharedGroup[] groups, ref int nextChild,
        ArrayPoolList<PartitionFold> workers, ref int nextUnit, IRefCountingMemoryProvider memoryProvider)
        where TFrame : struct, IGroupFrame<PbtStorageTreeKey, PbtStorageNodePath>
    {
        Span<byte> pathBuffer = stackalloc byte[1];
        Span<byte> childPathBuffer = stackalloc byte[1];
        PbtTraversalPath path = SharedPath(pathBuffer, group.Depth, group.PathBits);
        int childDepth = group.Depth + PbtGroupGeometry.LevelsPerGroup;
        for (int slot = group.Touched.NextSetBit(0); slot >= 0; slot = group.Touched.NextSetBit(slot + 1))
        {
            BoundaryNode boundary = TakeBoundary(ref frame, ref group.Hashes, path, ref group.Frontier, slot);
            if (childDepth == PbtGroupGeometry.ZoneGroupDepth)
            {
                ref PartitionFold.Unit unit = ref UnitAt(workers, nextUnit++);
                if (IsAbsentGroupBelow(boundary, childDepth)) unit.InheritedDescendantBytes = frame.DescendantBytes(slot);
                unit.Current = boundary.Owned(path);
                continue;
            }

            ref SharedGroup child = ref groups[nextChild++];
            child.Writer = PbtNodeGroupWriter<PbtStorageNodePath>.Rent(childDepth, memoryProvider);
            StoredGroupHashes.Open(out child.Hashes);
            child.Frontier = new(child.Touched);
            PbtTraversalPath childPath = SharedPath(childPathBuffer, childDepth, child.PathBits);
            if (IsAbsentGroup(boundary, childDepth))
            {
                child.IsAbsent = true;
                child.Absent = AbsentFrame(boundary, childPath, childDepth, frame.DescendantBytes(slot));
                Decompose(ref child.Absent, childPath, ref boundary, childDepth, ref child.Frontier, child.Touched);
            }
            else
            {
                child.Reader = new(store, childPath, boundary.HashAt(childPath, childDepth));
                Decompose(ref child.Reader, childPath, ref boundary, childDepth, ref child.Frontier, child.Touched);
            }
        }
    }

    /// <summary>Composes and publishes the shared group of <paramref name="frame"/>, leaving its root, with its size change, in the group's result.</summary>
    private static void ComposeShared<TFrame>(ref TFrame frame, ref SharedGroup group, PbtTraversalPath path, Span<FoldResult> results, IPbtNodeGroupSink sink)
        where TFrame : struct, IGroupFrame<PbtStorageTreeKey, PbtStorageNodePath>
    {
        int resultDepth = group.Depth - PbtGroupGeometry.LevelsPerGroup;
        Compose(ref frame, ref group.Hashes, group.Writer!, path, resultDepth, ref group.Frontier, results, ref group.Result);
        ValueHash256 groupHash = group.Result.Hash(path.Truncated(stackalloc byte[1], resultDepth), group.Depth);
        group.Result.SizeDelta = PublishGroup(sink, ref frame, group.Writer!, path, groupHash);
    }

    /// <summary>Runs every worker's fold, across threads as <paramref name="foldQuota"/> allows.</summary>
    private static void FoldWorkers(ArrayPoolList<PartitionFold> workers, ConcurrencyController foldQuota)
    {
        int nextWorker = 0;
        for (; nextWorker < workers.Count - 1 && !foldQuota.TryRequestConcurrencyQuota(); nextWorker++)
            workers[nextWorker].Fold();
        if (nextWorker < workers.Count - 1)
        {
            int callerThreadId = Environment.CurrentManagedThreadId;
            int admissionSlotClaimed = 0;
            try
            {
                Parallel.For(nextWorker, workers.Count,
                    () => TakeWorkerQuota(foldQuota, callerThreadId, ref admissionSlotClaimed),
                    (index, _, tookQuota) =>
                    {
                        workers[index].Fold();
                        return tookQuota;
                    },
                    tookQuota => ReturnWorkerQuota(foldQuota, tookQuota));
            }
            finally
            {
                ReturnAdmissionSlot(foldQuota, ref admissionSlotClaimed);
            }
        }
        else if (nextWorker < workers.Count)
        {
            workers[nextWorker].Fold();
        }
    }

    /// <summary>A group above the zones' units, with the frame it is rebuilt from.</summary>
    private struct SharedGroup
    {
        internal int Depth;
        /// <summary>The group's path, right-aligned: its first <see cref="Depth"/> bits.</summary>
        internal int PathBits;
        internal PbtBitmap Touched;
        /// <summary>Where this group's results start among every shared group's, one per touched slot.</summary>
        internal int ResultOffset;
        internal bool IsAbsent;
        internal GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> Reader;
        internal AbsentGroupFrame<PbtStorageTreeKey, PbtStorageNodePath> Absent;
        internal Frontier Frontier;
        internal StoredGroupHashes Hashes;
        internal PbtNodeGroupWriter<PbtStorageNodePath>? Writer;
        /// <summary>The composed root, anchored at the parent group's depth, carrying the size change of this group and everything below it.</summary>
        internal FoldResult Result;
    }

    private abstract class PartitionFold(byte zone, PartitionFold.Unit[] units) : IDisposable
    {
        internal byte Zone { get; } = zone;

        /// <summary>The zone's groups at <see cref="PbtGroupGeometry.ZoneGroupDepth"/> it folds, in path order.</summary>
        internal Unit[] Units { get; } = units;

        internal abstract void Fold();

        public abstract void Dispose();

        /// <summary>One group at <see cref="PbtGroupGeometry.ZoneGroupDepth"/> a worker folds, named by the key bits between the zone byte and it.</summary>
        internal struct Unit
        {
            internal int Bits;
            internal BoundaryNode Current;
            /// <summary>The shared frame's size below this unit's slot, when <see cref="Current"/> spans past the unit's group.</summary>
            internal long InheritedDescendantBytes;
            internal FoldResult Result;
        }
    }

    private sealed class PartitionFold<TKey, TPath>(IPbtStore store, byte zone,
        ArrayPoolList<PbtWriteOperation<TKey>> operations, ArrayPoolList<int> table,
        IRefCountingMemoryProvider memoryProvider, ConcurrencyController foldQuota, FoldFanOut fanOut,
        IMetricObserver? foldTime, StringLabel foldLabel) : PartitionFold(zone, CreateUnits(operations, table))
        where TKey : unmanaged, IPbtKey<TKey>
        where TPath : struct, IPbtNodePath<TPath>
    {
        /// <summary>The units the operations fall in, read from the producer's shards when the unit bits are a prefix of the shard nibble.</summary>
        private static Unit[] CreateUnits(ArrayPoolList<PbtWriteOperation<TKey>> operations, ArrayPoolList<int> table)
        {
            int unitBits = PbtGroupGeometry.UnitBits;
            ulong unitMask = 0;
            if (unitBits == 0) unitMask = 1;
            else if (unitBits <= 4)
                for (int shards = table[0]; shards != 0; shards &= shards - 1) unitMask |= 1UL << (BitOperations.TrailingZeroCount(shards) >> (4 - unitBits));
            else
                foreach (PbtWriteOperation<TKey> operation in operations.AsSpan()) unitMask |= 1UL << UnitOf(operation.Key);

            Unit[] units = new Unit[BitOperations.PopCount(unitMask)];
            for (int index = 0; unitMask != 0; unitMask &= unitMask - 1) units[index++].Bits = BitOperations.TrailingZeroCount(unitMask);
            return units;
        }

        /// <summary>The key bits between the zone byte and <see cref="PbtGroupGeometry.ZoneGroupDepth"/>, which never pass the key's second byte.</summary>
        private static int UnitOf(TKey key) => key.Bytes[1] >> (8 - PbtGroupGeometry.UnitBits);

        [SkipLocalsInit]
        internal override void Fold()
        {
            long start = Stopwatch.GetTimestamp();
            using IPbtConcurrentWriter concurrentWriter = store.CreateWriter();
            TrieUpdater<TKey, TPath>.FoldContext context = new(store, concurrentWriter, memoryProvider,
                foldQuota, operations.UnsafeGetInternalArray(), fanOut);
            // The producer grouped the zone by the nibble after the zone byte, so sorted shards sort the zone. With
            // four-level groups that nibble is the unit group's slot, and the shards keep its slot ranges.
            // Each shard's leaves are hashed with its sort, so the fold reads leaf hashes in place of values.
            TrieUpdater<TKey, TPath>.SortShards(context, operations.AsSpan(), table.AsSpan());
            ReadOnlySpan<int> slotTable = PbtGroupGeometry.LevelsPerGroup == 4 ? table.AsSpan() : default;
            ReadOnlySpan<PbtWriteOperation<TKey>> sorted = operations.AsSpan();
            int first = 0;
            foreach (ref Unit unit in Units.AsSpan())
            {
                int end = first + 1;
                if (PbtGroupGeometry.UnitBits == 0) end = sorted.Length;
                else while (end < sorted.Length && UnitOf(sorted[end].Key) == unit.Bits) end++;
                FoldUnit(context, ref unit, sorted[first..end], slotTable);
                first = end;
            }
            foldTime?.Observe(Stopwatch.GetTimestamp() - start, foldLabel);
        }

        [SkipLocalsInit]
        private void FoldUnit(TrieUpdater<TKey, TPath>.FoldContext context, ref Unit unit, ReadOnlySpan<PbtWriteOperation<TKey>> operations, ReadOnlySpan<int> slotTable)
        {
            int depth = PbtGroupGeometry.ZoneGroupDepth;
            Span<byte> pathBuffer = stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)];
            PbtTraversalPath path = new(pathBuffer);
            path.AppendBits(Zone, 8);
            path.AppendBits(unit.Bits, PbtGroupGeometry.UnitBits);
            TrieUpdater<TKey, TPath>.BoundaryNode current = TrieUpdater<TKey, TPath>.BoundaryNode.TakeFrom<PbtStorageTreeKey, PbtStorageNodePath>(ref unit.Current);
            if (TrieUpdater<TKey, TPath>.IsAbsentGroup(current, depth))
            {
                AbsentGroupFrame<TKey, TPath> absent = TrieUpdater<TKey, TPath>.AbsentFrame(current, path, depth, unit.InheritedDescendantBytes);
                FoldZone(context, ref absent, current, operations, ref path, slotTable, ref unit);
            }
            else
            {
                GroupFrameReader<TKey, TPath> reader = new(store, path, current.HashAt(path, depth));
                using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
                    FoldZone(context, ref reader, current, operations, ref path, slotTable, ref unit);
            }
        }

        [SkipLocalsInit]
        private void FoldZone<TFrame>(TrieUpdater<TKey, TPath>.FoldContext context, ref TFrame reader, scoped in TrieUpdater<TKey, TPath>.BoundaryNode current,
            ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, ReadOnlySpan<int> slotTable, ref Unit unit)
            where TFrame : struct, IGroupFrame<TKey, TPath>
        {
            int depth = PbtGroupGeometry.ZoneGroupDepth;
            int resultDepth = depth - PbtGroupGeometry.LevelsPerGroup;
            Span<byte> sourceBuffer = stackalloc byte[PbtStorageTreeKey.MaxLength];
            using PbtNodeGroupWriter<TPath> writer = PbtNodeGroupWriter<TPath>.Rent(depth, memoryProvider);
            TrieUpdater<TKey, TPath>.StoredGroupHashes.Open(out TrieUpdater<TKey, TPath>.StoredGroupHashes hashes);
            try
            {
                TrieUpdater<TKey, TPath>.FoldResult result = default;
                TrieUpdater<TKey, TPath>.FoldZoneSorted(context, ref reader, ref hashes, writer, current, operations, ref path, resultDepth, slotTable, ref result);
                // The result is anchored at the shared group its boundary slot sits in, one group above this one.
                ValueHash256 groupHash = result.Hash(path.Truncated(sourceBuffer, resultDepth), depth);
                result.SizeDelta = TrieUpdater<TKey, TPath>.PublishGroup(context.Writer, ref reader, writer, path, groupHash);
                FoldResult.TakeFrom<TKey, TPath>(ref result, ref unit.Result);
            }
            finally
            {
                hashes.Dispose();
            }
        }

        public override void Dispose()
        {
            operations.Dispose();
            table.Dispose();
        }
    }
}
