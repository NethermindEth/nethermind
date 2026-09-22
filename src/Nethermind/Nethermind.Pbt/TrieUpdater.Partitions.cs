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
using static Nethermind.Pbt.TrieUpdater;
using static Nethermind.Pbt.TrieUpdater<Nethermind.Pbt.PbtStorageTreeKey, Nethermind.Pbt.PbtStorageNodePath>;

namespace Nethermind.Pbt;

public static partial class TrieUpdater
{
    private static readonly StringLabel _accountFoldLabel = new("account");
    private static readonly StringLabel _codeFoldLabel = new("code");
    private static readonly StringLabel _storageFoldLabel = new("storage");

    /// <summary>Applies an account/code key batch to a tree containing only small keys.</summary>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<PbtPath> changes) =>
        TrieUpdater<PbtPath, PbtNodePath>.UpdateRoot(store, currentRoot, changes);

    /// <summary>Applies a storage-capable key batch to a complete tree.</summary>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<PbtStorageTreeKey> changes) =>
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.UpdateRoot(store, currentRoot, changes);

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<PbtPath> changes,
        TrieUpdaterMetrics? metrics, IRefCountingMemoryProvider? memoryProvider = null) =>
        TrieUpdater<PbtPath, PbtNodePath>.UpdateRoot(store, currentRoot, changes, metrics, memoryProvider);

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<PbtStorageTreeKey> changes,
        TrieUpdaterMetrics? metrics, IRefCountingMemoryProvider? memoryProvider = null) =>
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.UpdateRoot(store, currentRoot, changes, metrics, memoryProvider);

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatchSet<PbtPath> changes,
        TrieUpdaterMetrics? metrics = null, IRefCountingMemoryProvider? memoryProvider = null) =>
        TrieUpdater<PbtPath, PbtNodePath>.UpdateRoot(store, currentRoot, changes, metrics, memoryProvider);

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatchSet<PbtStorageTreeKey> changes,
        TrieUpdaterMetrics? metrics = null, IRefCountingMemoryProvider? memoryProvider = null) =>
        TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.UpdateRoot(store, currentRoot, changes, metrics, memoryProvider);

    /// <summary>Folds disjoint partitions concurrently before merging their shared ancestors.</summary>
    /// <remarks>
    /// The zones and the touched buckets of every frame wide enough to fan out, merged into runs of at least the
    /// minimum <paramref name="fanOut"/> gives for the frame's subtree size, share <paramref name="foldQuota"/>: a fan-out folds its
    /// parts on the calling thread while no slot is free, and the first slot it takes admits a parallel loop over
    /// the parts still left, whose workers charge themselves as they start; a quota of one folds everything
    /// serially. Each zone's fold time is observed on <paramref name="partitionFoldTime"/> labelled by partition, so
    /// an imbalance between them is visible. Groups rewritten by this fold leave prefixless interior branches
    /// implicit as <paramref name="prefixlessBranchOmission"/> selects; untouched groups keep their layout.
    /// The supplied store must support concurrent reads and writes. Failed
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
        TrieUpdaterMetrics? metrics = null,
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
            using ArrayPoolListRef<Frontier> zoneFrontiers = new(16, 16);
            Frontier rootFrontier = default;
            Span<int> touchedZoneMasks = stackalloc int[16];
            touchedZoneMasks.Clear();
            try
            {
                AddWorker<PbtPath, PbtNodePath>(changes.Account, Eip8297KeyDerivation.AccountZone, _accountFoldLabel);
                AddWorker<PbtPath, PbtNodePath>(changes.Code, Eip8297KeyDerivation.CodeZone, _codeFoldLabel);
                AddWorker<PbtStoragePath, PbtStorageNodePath>(changes.Storage, Eip8297KeyDerivation.StorageZone, _storageFoldLabel);
                if (workers.Count == 0) return currentRoot;

                GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> rootReader = new(store, 0, currentRoot, metrics);
                using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref rootReader))
                {
                    using PbtNodeGroupWriter<PbtStorageNodePath> rootWriter = new(0, memoryProvider, prefixlessBranchOmission);
                    PbtTraversalPath rootPath = new(Span<byte>.Empty);
                    Span<byte> sharedPathBuffer = stackalloc byte[1];
                    Span<byte> sourceBuffer = stackalloc byte[PbtStorageTreeKey.MaxLength];
                    int touchedRootMask = 0;
                    foreach (PartitionFold worker in workers)
                    {
                        touchedRootMask |= 1 << (worker.Zone >> 4);
                        touchedZoneMasks[worker.Zone >> 4] |= 1 << (worker.Zone & 15);
                    }
                    TraversalSubtree root = new(rootPath, rootReader.Take(rootPath, rootWriter, PbtFourLevelGroupGeometry.RootPosition, allowAbsent: true));
                    Decompose(ref rootReader, rootWriter, rootPath, ref root, 0, ref rootFrontier, touchedRootMask);
                    foreach (PartitionFold worker in workers)
                    {
                        int slot = worker.Zone >> 4;
                        PbtTraversalPath sharedPath = new(sharedPathBuffer);
                        sharedPath.AppendMut(slot);
                        ref GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> sharedReader = ref sharedReaders.AsSpan()[slot];
                        if (sharedWriters[slot] is not { } sharedWriter)
                        {
                            TraversalSubtree boundary = TakeBoundary(ref rootReader, rootWriter, rootPath, ref rootFrontier, slot, sourceBuffer);
                            sharedReader = new(store, 4, boundary.Hash(4, metrics), metrics);
                            sharedWriter = new(4, memoryProvider, prefixlessBranchOmission);
                            sharedWriters[slot] = sharedWriter;
                            ResolveAbsentGroup(ref sharedReader, in rootReader, sharedPath, boundary);
                            Decompose(ref sharedReader, sharedWriter, sharedPath, ref boundary, 4, ref zoneFrontiers.AsSpan()[slot], touchedZoneMasks[slot]);
                        }
                        TraversalSubtree workerBoundary = TakeBoundary(ref sharedReader, sharedWriter, sharedPath, ref zoneFrontiers.AsSpan()[slot], worker.Zone & 15, sourceBuffer);
                        // Workers fold on other threads, so the shared frame's size is handed over here; the frame is
                        // resolved whenever the boundary is a branch, which is the only case that inherits it.
                        if (IsAbsentGroupBelow(workerBoundary, 8)) worker.InheritedDescendantBytes = sharedReader.DescendantBytes(worker.Zone & 15);
                        worker.Current = workerBoundary.Materialize();
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
                        PbtTraversalPath sharedPath = new(sharedPathBuffer);
                        sharedPath.AppendMut(worker.Zone >> 4);
                        TraversalSubtree workerResult = worker.Result.Borrow(sourceBuffer);
                        SetBoundary(sharedPath, ref zoneFrontiers.AsSpan()[worker.Zone >> 4], worker.Zone & 15, ref workerResult);
                        sharedWriters[worker.Zone >> 4]!.AddDescendantDelta(worker.Zone & 15, worker.Result.SizeDelta);
                        worker.Result = default;
                        if (worker.Metrics is { } workerMetrics) metrics!.Add(workerMetrics);
                    }
                    for (int slot = 0; slot < sharedReaders.Count; slot++)
                    {
                        if (sharedWriters[slot] is not { } sharedWriter) continue;
                        PbtTraversalPath sharedPath = new(sharedPathBuffer);
                        sharedPath.AppendMut(slot);
                        ref GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> sharedReader = ref sharedReaders.AsSpan()[slot];
                        TraversalSubtree composed = Compose(ref sharedReader, sharedWriter, sharedPath, metrics, ref zoneFrontiers.AsSpan()[slot], sourceBuffer);
                        ValueHash256 groupHash = composed.Hash(4, metrics);
                        long zoneDelta = PublishGroup(store, ref sharedReader, sharedWriter, sharedPath, groupHash);
                        SetBoundary(rootPath, ref rootFrontier, slot, ref composed);
                        rootWriter.AddDescendantDelta(slot, zoneDelta);
                    }
                    TraversalSubtree result = Compose(ref rootReader, rootWriter, rootPath, metrics, ref rootFrontier, sourceBuffer);
                    ValueHash256 hash = rootWriter.Write(rootPath, PbtFourLevelGroupGeometry.RootPosition, 0, ref result, metrics);
                    PublishGroup(store, ref rootReader, rootWriter, rootPath, hash);
                    return hash;
                }
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
                where TKey : struct, IPbtKey<TKey>
                where TPath : struct, IPbtNodePath<TPath>
            {
                if (batch is null) return;
                ArgumentOutOfRangeException.ThrowIfNotEqual(batch.ShardNibbleIndex, 2);
                batch.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table);
                PartitionFold<TKey, TPath> worker = new(store, zone, operations, table, metrics is not null, memoryProvider, foldQuota, fanOut, prefixlessBranchOmission, partitionFoldTime, foldLabel);
                if (operations.Count != 0) workers.Add(worker);
                else worker.Dispose();
            }
        }
    }

    private abstract class PartitionFold(byte zone, bool collectMetrics) : IDisposable
    {
        internal byte Zone { get; } = zone;
        internal TrieUpdaterMetrics? Metrics { get; } = collectMetrics ? new() : null;
        internal OwnedSubtree Current;
        internal OwnedSubtree Result;
        /// <summary>The shared frame's size below this worker's slot, when <see cref="Current"/> spans past the worker's group.</summary>
        internal long InheritedDescendantBytes;

        internal abstract void Fold();

        public abstract void Dispose();
    }

    private sealed class PartitionFold<TKey, TPath>(IPbtStore store, byte zone,
        ArrayPoolList<PbtWriteOperation<TKey>> operations, ArrayPoolList<int> table,
        bool collectMetrics, IRefCountingMemoryProvider memoryProvider, ConcurrencyController foldQuota, FoldFanOut fanOut,
        PbtPrefixlessBranchOmission prefixlessBranchOmission, IMetricObserver? foldTime, StringLabel foldLabel) : PartitionFold(zone, collectMetrics)
        where TKey : struct, IPbtKey<TKey>
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
            Span<byte> sourceBuffer = stackalloc byte[PbtStorageTreeKey.MaxLength];
            GroupFrameReader<TKey, TPath> reader = new(store, 8, Current.Borrow(sourceBuffer).Hash(8, Metrics), Metrics);
            using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
            {
                using PbtNodeGroupWriter<TPath> writer = new(8, memoryProvider, prefixlessBranchOmission);
                TrieUpdater<TKey, TPath>.OwnedSubtree ownedCurrent = TrieUpdater<TKey, TPath>.OwnedSubtree.TakeFrom<PbtStorageTreeKey, PbtStorageNodePath>(ref Current);
                TrieUpdater<TKey, TPath>.TraversalSubtree current = ownedCurrent.Borrow(sourceBuffer);
                TrieUpdater<TKey, TPath>.OwnedSubtree result = default;
                TrieUpdater<TKey, TPath>.FoldContext context = new(store, memoryProvider, Metrics,
                    foldQuota, operations.UnsafeGetInternalArray(), fanOut, prefixlessBranchOmission);
                TrieUpdater<TKey, TPath>.ResolveAbsentGroup(ref reader, current, InheritedDescendantBytes);
                // Consume the producer's nibble bounds before filtering deletes or comparing deeper key prefixes.
                result = TrieUpdater<TKey, TPath>.FoldBoundary(context, ref reader, writer, current,
                    operations.AsSpan(), ref path, 8, new(table.AsSpan(), 8, false));
                ValueHash256 groupHash = result.Borrow(sourceBuffer).Hash(8, Metrics);
                result.SizeDelta = TrieUpdater<TKey, TPath>.PublishGroup(store, ref reader, writer, path, groupHash);
                Result = OwnedSubtree.TakeFrom<TKey, TPath>(ref result);
            }
            foldTime?.Observe(Stopwatch.GetTimestamp() - start, foldLabel);
        }

        public override void Dispose()
        {
            operations.Dispose();
            table.Dispose();
        }
    }
}

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : struct, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    [SkipLocalsInit]
    internal static OwnedSubtree FoldBoundary(
        FoldContext context,
        ref GroupFrameReader<TKey, TPath> reader,
        PbtNodeGroupWriter<TPath> writer,
        scoped TraversalSubtree current,
        Span<PbtWriteOperation<TKey>> operations,
        ref PbtTraversalPath path,
        int bitDepth,
        BucketPlan plan)
    {
        Span<byte> buffer = stackalloc byte[plan.GetBufferSize(operations.Length, bitDepth)];
        PartitionOutcome partition = plan.WithBuffer(buffer).BucketSort(operations, bitDepth, context.Metrics);
        return FoldBoundaryFromPartition(context, ref reader, writer, current, operations, ref path, bitDepth, partition);
    }
}
