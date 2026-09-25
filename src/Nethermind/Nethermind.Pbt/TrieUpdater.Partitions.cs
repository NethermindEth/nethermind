// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    /// an imbalance between them is visible. Groups rewritten by this fold leave prefixless interior branches
    /// implicit as <paramref name="prefixlessBranchOmission"/> selects; untouched groups keep their layout.
    /// Each zone's shards are sorted in place, and the producer's shard counts serve as the zone group's slot ranges.
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
        PbtPrefixlessBranchOmission prefixlessBranchOmission,
        IMetricObserver? partitionFoldTime,
        IRefCountingMemoryProvider? memoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(foldQuota);
        using ArrayPoolList<PartitionFold> workers = new(3);
        using ArrayPoolListRef<GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>> sharedReaders = new(16, 16);
        using ArrayPoolListRef<PbtNodeGroupWriter<PbtStorageNodePath>?> sharedWriters = new(16, 16);
        using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(sharedReaders.AsSpan()))
        {
            memoryProvider ??= PooledRefCountingMemoryProvider.Instance;
            using IPbtConcurrentWriter storeWriter = store.CreateWriter();
            using ArrayPoolListRef<Frontier> zoneFrontiers = new(16, 16);
            Span<int> touchedZoneMasks = stackalloc int[16];
            touchedZoneMasks.Clear();
            try
            {
                AddWorker<PbtPath, PbtNodePath>(changes.Account, Eip8297KeyDerivation.AccountZone, _accountFoldLabel);
                AddWorker<PbtPath, PbtNodePath>(changes.Code, Eip8297KeyDerivation.CodeZone, _codeFoldLabel);
                AddWorker<PbtStoragePath, PbtStorageNodePath>(changes.Storage, Eip8297KeyDerivation.StorageZone, _storageFoldLabel);
                if (workers.Count == 0) return currentRoot;

                PbtTraversalPath rootPath = new(Span<byte>.Empty);
                if (!GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.TryLoad(store, rootPath, currentRoot,
                        out GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> rootReader))
                {
                    AbsentGroupFrame<PbtStorageTreeKey, PbtStorageNodePath> emptyRoot = new(0);
                    return FoldZones(store, ref emptyRoot, default, workers, sharedReaders.AsSpan(), sharedWriters.AsSpan(), zoneFrontiers.AsSpan(),
                        touchedZoneMasks, storeWriter, foldQuota, memoryProvider, prefixlessBranchOmission);
                }
                using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref rootReader))
                    return FoldZones(store, ref rootReader, rootReader.TakeRoot(), workers, sharedReaders.AsSpan(), sharedWriters.AsSpan(), zoneFrontiers.AsSpan(),
                        touchedZoneMasks, storeWriter, foldQuota, memoryProvider, prefixlessBranchOmission);
            }
            finally
            {
                foreach (PartitionFold worker in workers) worker.Dispose();
                for (int slot = 0; slot < sharedReaders.Count; slot++)
                {
                    sharedWriters[slot]?.Dispose();
                }
            }

            void AddWorker<TKey, TPath>(PbtWriteBatch<TKey>? batch, byte zone, StringLabel foldLabel)
                where TKey : unmanaged, IPbtKey<TKey>
                where TPath : struct, IPbtNodePath<TPath>
            {
                if (batch is null) return;
                ArgumentOutOfRangeException.ThrowIfNotEqual(batch.ShardNibbleIndex, 2);
                batch.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table);
                PartitionFold<TKey, TPath> worker = new(store, zone, operations, table, memoryProvider, foldQuota, fanOut, prefixlessBranchOmission,
                    partitionFoldTime, foldLabel);
                if (operations.Count != 0) workers.Add(worker);
                else worker.Dispose();
            }
        }
    }

    /// <summary>Folds the workers' zones below the root group of <paramref name="rootReader"/>, then composes and publishes the groups above them.</summary>
    [SkipLocalsInit]
    private static ValueHash256 FoldZones<TRoot>(IPbtStore store, ref TRoot rootReader, BoundaryNode root, ArrayPoolList<PartitionFold> workers,
        Span<GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>> sharedReaders, Span<PbtNodeGroupWriter<PbtStorageNodePath>?> sharedWriters,
        Span<Frontier> zoneFrontiers, Span<int> touchedZoneMasks, IPbtConcurrentWriter storeWriter, ConcurrencyController foldQuota,
        IRefCountingMemoryProvider memoryProvider, PbtPrefixlessBranchOmission prefixlessBranchOmission)
        where TRoot : struct, IGroupFrame<PbtStorageTreeKey, PbtStorageNodePath>
    {
        using PbtNodeGroupWriter<PbtStorageNodePath> rootWriter = PbtNodeGroupWriter<PbtStorageNodePath>.Rent(0, memoryProvider, prefixlessBranchOmission);
        PbtTraversalPath rootPath = new(Span<byte>.Empty);
        Span<byte> sharedPathBuffer = stackalloc byte[1];
        int touchedRootMask = 0;
        foreach (PartitionFold worker in workers)
        {
            touchedRootMask |= 1 << (worker.Zone >> 4);
            touchedZoneMasks[worker.Zone >> 4] |= 1 << (worker.Zone & 15);
        }
        Frontier rootFrontier = new(touchedRootMask);
        Span<FoldResult> rootResults = stackalloc FoldResult[BitOperations.PopCount((uint)touchedRootMask)];
        Span<FoldResult> zoneResults = stackalloc FoldResult[workers.Count];
        Span<AbsentGroupFrame<PbtStorageTreeKey, PbtStorageNodePath>> absentZones = stackalloc AbsentGroupFrame<PbtStorageTreeKey, PbtStorageNodePath>[sharedReaders.Length];
        Span<StoredGroupHashes> zoneHashes = stackalloc StoredGroupHashes[sharedReaders.Length];
        int absentZoneMask = 0;
        StoredGroupHashes rootHashes = default;
        Decompose(ref rootReader, rootPath, ref root, 0, ref rootFrontier, touchedRootMask);
        foreach (PartitionFold worker in workers)
        {
            int slot = worker.Zone >> 4;
            PbtTraversalPath sharedPath = new(sharedPathBuffer);
            sharedPath.AppendMut(slot);
            if (sharedWriters[slot] is null)
            {
                BoundaryNode boundary = TakeBoundary(ref rootReader, ref rootHashes, rootPath, ref rootFrontier, slot);
                sharedWriters[slot] = PbtNodeGroupWriter<PbtStorageNodePath>.Rent(4, memoryProvider, prefixlessBranchOmission);
                zoneHashes[slot] = default;
                zoneFrontiers[slot] = new(touchedZoneMasks[slot]);
                if (IsAbsentGroup(boundary, 4))
                {
                    absentZoneMask |= 1 << slot;
                    absentZones[slot] = AbsentFrame(boundary, sharedPath, 4, rootReader.DescendantBytes(slot));
                    Decompose(ref absentZones[slot], sharedPath, ref boundary, 4, ref zoneFrontiers[slot], touchedZoneMasks[slot]);
                }
                else
                {
                    sharedReaders[slot] = new(store, sharedPath, boundary.HashAt(sharedPath, 4));
                    Decompose(ref sharedReaders[slot], sharedPath, ref boundary, 4, ref zoneFrontiers[slot], touchedZoneMasks[slot]);
                }
            }
            if ((absentZoneMask >> slot & 1) != 0) TakeWorkerBoundary(ref absentZones[slot], ref zoneHashes[slot], sharedPath, ref zoneFrontiers[slot], worker);
            else TakeWorkerBoundary(ref sharedReaders[slot], ref zoneHashes[slot], sharedPath, ref zoneFrontiers[slot], worker);
        }

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

        foreach (PartitionFold worker in workers)
        {
            sharedWriters[worker.Zone >> 4]!.AddDescendantDelta(worker.Zone & 15, worker.Result.SizeDelta);
            SetBoundary(ref zoneFrontiers[worker.Zone >> 4], ZoneResults(zoneResults, touchedZoneMasks, worker.Zone >> 4), worker.Zone & 15, ref worker.Result);
        }
        for (int slot = 0; slot < sharedWriters.Length; slot++)
        {
            if (sharedWriters[slot] is not { } sharedWriter) continue;
            PbtTraversalPath sharedPath = new(sharedPathBuffer);
            sharedPath.AppendMut(slot);
            FoldResult zoneRoot = default;
            Span<FoldResult> results = ZoneResults(zoneResults, touchedZoneMasks, slot);
            long zoneDelta = (absentZoneMask >> slot & 1) != 0
                ? ComposeZone(ref absentZones[slot], ref zoneHashes[slot], sharedWriter, sharedPath, ref zoneFrontiers[slot], results, storeWriter, ref zoneRoot)
                : ComposeZone(ref sharedReaders[slot], ref zoneHashes[slot], sharedWriter, sharedPath, ref zoneFrontiers[slot], results, storeWriter, ref zoneRoot);
            SetBoundary(ref rootFrontier, rootResults, slot, ref zoneRoot);
            rootWriter.AddDescendantDelta(slot, zoneDelta);
        }
        FoldResult rootResult = default;
        Compose(ref rootReader, ref rootHashes, rootWriter, rootPath, 0, ref rootFrontier, rootResults, ref rootResult);
        ValueHash256 hash = rootWriter.WriteRoot(rootPath, rootResult);
        PublishGroup(storeWriter, ref rootReader, rootWriter, rootPath, hash);
        return hash;
    }

    /// <summary>Hands <paramref name="worker"/> its boundary node from the zone group of <paramref name="zoneReader"/>.</summary>
    /// <remarks>Workers fold on other threads, so the zone frame's size is handed over here too, to the only boundary that inherits it.</remarks>
    private static void TakeWorkerBoundary<TFrame>(ref TFrame zoneReader, ref StoredGroupHashes zoneHashes, scoped in PbtTraversalPath zonePath,
        ref Frontier zoneFrontier, PartitionFold worker)
        where TFrame : struct, IGroupFrame<PbtStorageTreeKey, PbtStorageNodePath>
    {
        BoundaryNode boundary = TakeBoundary(ref zoneReader, ref zoneHashes, zonePath, ref zoneFrontier, worker.Zone & 15);
        if (IsAbsentGroupBelow(boundary, 8)) worker.InheritedDescendantBytes = zoneReader.DescendantBytes(worker.Zone & 15);
        worker.Current = boundary.Owned();
    }

    /// <summary>Composes and publishes the zone group of <paramref name="zoneReader"/>, returning its size change and leaving its root in <paramref name="zoneRoot"/>.</summary>
    private static long ComposeZone<TFrame>(ref TFrame zoneReader, ref StoredGroupHashes zoneHashes, PbtNodeGroupWriter<PbtStorageNodePath> zoneWriter,
        PbtTraversalPath zonePath, ref Frontier zoneFrontier, Span<FoldResult> zoneResults, IPbtNodeGroupSink sink,
        ref FoldResult zoneRoot)
        where TFrame : struct, IGroupFrame<PbtStorageTreeKey, PbtStorageNodePath>
    {
        Compose(ref zoneReader, ref zoneHashes, zoneWriter, zonePath, 0, ref zoneFrontier, zoneResults, ref zoneRoot);
        ValueHash256 groupHash = zoneRoot.Hash(new PbtTraversalPath(Span<byte>.Empty), 4);
        return PublishGroup(sink, ref zoneReader, zoneWriter, zonePath, groupHash);
    }

    /// <summary>The results of the zone group at root <paramref name="slot"/>, one per touched zone, laid out after the lower slots' own.</summary>
    private static Span<FoldResult> ZoneResults(Span<FoldResult> results, ReadOnlySpan<int> touchedZoneMasks, int slot)
    {
        int offset = 0;
        foreach (int mask in touchedZoneMasks[..slot]) offset += BitOperations.PopCount((uint)mask);
        return results.Slice(offset, BitOperations.PopCount((uint)touchedZoneMasks[slot]));
    }

    private abstract class PartitionFold(byte zone) : IDisposable
    {
        internal byte Zone { get; } = zone;
        internal BoundaryNode Current;
        internal FoldResult Result;
        /// <summary>The shared frame's size below this worker's slot, when <see cref="Current"/> spans past the worker's group.</summary>
        internal long InheritedDescendantBytes;

        internal abstract void Fold();

        public abstract void Dispose();
    }

    private sealed class PartitionFold<TKey, TPath>(IPbtStore store, byte zone,
        ArrayPoolList<PbtWriteOperation<TKey>> operations, ArrayPoolList<int> table,
        IRefCountingMemoryProvider memoryProvider, ConcurrencyController foldQuota, FoldFanOut fanOut,
        PbtPrefixlessBranchOmission prefixlessBranchOmission, IMetricObserver? foldTime, StringLabel foldLabel) : PartitionFold(zone)
        where TKey : unmanaged, IPbtKey<TKey>
        where TPath : struct, IPbtNodePath<TPath>
    {
        [SkipLocalsInit]
        internal override void Fold()
        {
            long start = Stopwatch.GetTimestamp();
            Span<byte> pathBuffer = stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)];
            PbtTraversalPath path = new(pathBuffer);
            path.AppendMut(Zone >> 4);
            path.AppendMut(Zone & 15);
            TrieUpdater<TKey, TPath>.BoundaryNode current = TrieUpdater<TKey, TPath>.BoundaryNode.TakeFrom<PbtStorageTreeKey, PbtStorageNodePath>(ref Current);
            if (TrieUpdater<TKey, TPath>.IsAbsentGroup(current, 8))
            {
                AbsentGroupFrame<TKey, TPath> absent = TrieUpdater<TKey, TPath>.AbsentFrame(current, path, 8, InheritedDescendantBytes);
                FoldZone(ref absent, current, ref path);
            }
            else
            {
                GroupFrameReader<TKey, TPath> reader = new(store, path, current.HashAt(path, 8));
                using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
                    FoldZone(ref reader, current, ref path);
            }
            foldTime?.Observe(Stopwatch.GetTimestamp() - start, foldLabel);
        }

        [SkipLocalsInit]
        private void FoldZone<TFrame>(ref TFrame reader, scoped in TrieUpdater<TKey, TPath>.BoundaryNode current, ref PbtTraversalPath path)
            where TFrame : struct, IGroupFrame<TKey, TPath>
        {
            Span<byte> sourceBuffer = stackalloc byte[PbtStorageTreeKey.MaxLength];
            using PbtNodeGroupWriter<TPath> writer = PbtNodeGroupWriter<TPath>.Rent(8, memoryProvider, prefixlessBranchOmission);
            using IPbtConcurrentWriter concurrentWriter = store.CreateWriter();
            TrieUpdater<TKey, TPath>.FoldContext context = new(store, concurrentWriter, memoryProvider,
                foldQuota, operations.UnsafeGetInternalArray(), fanOut, prefixlessBranchOmission);
            TrieUpdater<TKey, TPath>.StoredGroupHashes hashes = default;
            TrieUpdater<TKey, TPath>.FoldResult result = default;
            // The producer grouped the zone by the slot nibble of this group, so sorted shards sort the zone and keep its slot ranges.
            TrieUpdater<TKey, TPath>.SortShards(context, operations.AsSpan(), table.AsSpan());
            TrieUpdater<TKey, TPath>.FoldZoneSorted(context, ref reader, ref hashes, writer, current, operations.AsSpan(), ref path, 4, table.AsSpan(), ref result);
            // The result is anchored at the zone cursor its boundary slot sits on, four bits above this group.
            ValueHash256 groupHash = result.Hash(path.Truncated(sourceBuffer, 4), 8);
            result.SizeDelta = TrieUpdater<TKey, TPath>.PublishGroup(concurrentWriter, ref reader, writer, path, groupHash);
            FoldResult.TakeFrom<TKey, TPath>(ref result, ref Result);
        }

        public override void Dispose()
        {
            operations.Dispose();
            table.Dispose();
        }
    }
}
