// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Attributes;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using static Nethermind.Pbt.TrieUpdater;
using static Nethermind.Pbt.TrieUpdater<Nethermind.Pbt.PbtTreeKey, Nethermind.Pbt.PbtStorageNodePath>;

namespace Nethermind.Pbt;

public static partial class TrieUpdater
{
    private static readonly StringLabel _accountFoldLabel = new("account");
    private static readonly StringLabel _codeFoldLabel = new("code");
    private static readonly StringLabel _storageFoldLabel = new("storage");

    /// <summary>Applies an account/code key batch to a tree containing only small keys.</summary>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<PbtFullKey> changes) =>
        TrieUpdater<PbtFullKey, PbtNodePath>.UpdateRoot(store, currentRoot, changes);

    /// <summary>Applies a storage-capable key batch to a complete tree.</summary>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<PbtTreeKey> changes) =>
        TrieUpdater<PbtTreeKey, PbtStorageNodePath>.UpdateRoot(store, currentRoot, changes);

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<PbtFullKey> changes,
        TrieUpdaterMetrics? metrics, IRefCountingMemoryProvider? memoryProvider = null) =>
        TrieUpdater<PbtFullKey, PbtNodePath>.UpdateRoot(store, currentRoot, changes, metrics, memoryProvider);

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<PbtTreeKey> changes,
        TrieUpdaterMetrics? metrics, IRefCountingMemoryProvider? memoryProvider = null) =>
        TrieUpdater<PbtTreeKey, PbtStorageNodePath>.UpdateRoot(store, currentRoot, changes, metrics, memoryProvider);

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatchSet<PbtFullKey> changes,
        TrieUpdaterMetrics? metrics = null, IRefCountingMemoryProvider? memoryProvider = null) =>
        TrieUpdater<PbtFullKey, PbtNodePath>.UpdateRoot(store, currentRoot, changes, metrics, memoryProvider);

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatchSet<PbtTreeKey> changes,
        TrieUpdaterMetrics? metrics = null, IRefCountingMemoryProvider? memoryProvider = null) =>
        TrieUpdater<PbtTreeKey, PbtStorageNodePath>.UpdateRoot(store, currentRoot, changes, metrics, memoryProvider);

    /// <summary>Folds disjoint partitions concurrently before merging their shared ancestors.</summary>
    /// <remarks>
    /// The zones fold under <paramref name="foldOptions"/>, and so do the touched buckets of every frame wide enough
    /// to fan out; a degree of one folds everything serially. Each zone's fold time is observed on
    /// <paramref name="partitionFoldTime"/> labelled by partition, so an imbalance between them is visible. The
    /// supplied store must support concurrent reads and writes. Failed folds may leave partial writes; the caller
    /// owns failure isolation and must not reuse that state without recovery.
    /// </remarks>
    [SkipLocalsInit]
    internal static ValueHash256 UpdateRoot(
        IPbtStore store,
        in ValueHash256 currentRoot,
        PbtPartitionBatches changes,
        ParallelOptions foldOptions,
        IMetricObserver? partitionFoldTime,
        TrieUpdaterMetrics? metrics = null,
        IRefCountingMemoryProvider? memoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(foldOptions);
        using ArrayPoolList<PartitionFold> workers = new(3);
        using ArrayPoolListRef<GroupFrameReader<PbtTreeKey, PbtStorageNodePath>> sharedReaders = new(16, 16);
        using ArrayPoolListRef<PbtNodeGroupWriter<PbtStorageNodePath>?> sharedWriters = new(16, 16);
        using (new GroupFrameReader<PbtTreeKey, PbtStorageNodePath>.Scope(sharedReaders.AsSpan()))
        {
            memoryProvider ??= PooledRefCountingMemoryProvider.Instance;
            using ArrayPoolListRef<Frontier> zoneFrontiers = new(16, 16);
            Frontier rootFrontier = default;
            Span<int> touchedZoneMasks = stackalloc int[16];
            touchedZoneMasks.Clear();
            try
            {
                AddWorker<PbtFullKey, PbtNodePath>(changes.Account, Eip8297KeyDerivation.AccountZone, _accountFoldLabel);
                AddWorker<PbtFullKey, PbtNodePath>(changes.Code, Eip8297KeyDerivation.CodeZone, _codeFoldLabel);
                AddWorker<PbtStorageFullKey, PbtStorageNodePath>(changes.Storage, Eip8297KeyDerivation.StorageZone, _storageFoldLabel);
                if (workers.Count == 0) return currentRoot;

                GroupFrameReader<PbtTreeKey, PbtStorageNodePath> rootReader = new(store, 0, currentRoot, metrics);
                using (new GroupFrameReader<PbtTreeKey, PbtStorageNodePath>.Scope(ref rootReader))
                {
                    using PbtNodeGroupWriter<PbtStorageNodePath> rootWriter = new(0, memoryProvider);
                    PbtTraversalPath rootPath = new(Span<byte>.Empty);
                    Span<byte> sharedPathBuffer = stackalloc byte[1];
                    Span<byte> sourceBuffer = stackalloc byte[PbtTreeKey.MaxLength];
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
                        ref GroupFrameReader<PbtTreeKey, PbtStorageNodePath> sharedReader = ref sharedReaders.AsSpan()[slot];
                        if (sharedWriters[slot] is not { } sharedWriter)
                        {
                            TraversalSubtree boundary = TakeBoundary(ref rootReader, rootWriter, rootPath, ref rootFrontier, slot, sourceBuffer);
                            sharedReader = new(store, 4, boundary.Hash(4, metrics), metrics);
                            sharedWriter = new(4, memoryProvider);
                            sharedWriters[slot] = sharedWriter;
                            Decompose(ref sharedReader, sharedWriter, sharedPath, ref boundary, 4, ref zoneFrontiers.AsSpan()[slot], touchedZoneMasks[slot]);
                        }
                        worker.Current = TakeBoundary(ref sharedReader, sharedWriter, sharedPath, ref zoneFrontiers.AsSpan()[slot], worker.Zone & 15, sourceBuffer).Materialize();
                    }

                    Parallel.ForEach(workers, foldOptions, static worker => worker.Fold());

                    foreach (PartitionFold worker in workers)
                    {
                        PbtTraversalPath sharedPath = new(sharedPathBuffer);
                        sharedPath.AppendMut(worker.Zone >> 4);
                        TraversalSubtree workerResult = worker.Result.Borrow(sourceBuffer);
                        SetBoundary(sharedPath, ref zoneFrontiers.AsSpan()[worker.Zone >> 4], worker.Zone & 15, ref workerResult);
                        worker.Result = default;
                        if (worker.Metrics is { } workerMetrics) metrics!.Add(workerMetrics);
                    }
                    for (int slot = 0; slot < sharedReaders.Count; slot++)
                    {
                        if (sharedWriters[slot] is not { } sharedWriter) continue;
                        PbtTraversalPath sharedPath = new(sharedPathBuffer);
                        sharedPath.AppendMut(slot);
                        ref GroupFrameReader<PbtTreeKey, PbtStorageNodePath> sharedReader = ref sharedReaders.AsSpan()[slot];
                        TraversalSubtree composed = Compose(ref sharedReader, sharedWriter, sharedPath, metrics, ref zoneFrontiers.AsSpan()[slot], sourceBuffer);
                        ValueHash256 groupHash = composed.Hash(4, metrics);
                        SetBoundary(rootPath, ref rootFrontier, slot, ref composed);
                        using RefCountingMemory? payload = sharedWriter.Detach();
                        store.SetNodeGroup(sharedPath, groupHash, payload);
                    }
                    TraversalSubtree result = Compose(ref rootReader, rootWriter, rootPath, metrics, ref rootFrontier, sourceBuffer);
                    ValueHash256 hash = rootWriter.Write(rootPath, PbtFourLevelGroupGeometry.RootPosition, 0, ref result, metrics);
                    using (RefCountingMemory? payload = rootWriter.Detach())
                        store.SetNodeGroup(rootPath, hash, payload);
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
                PartitionFold<TKey, TPath> worker = new(store, zone, operations, table, metrics is not null, memoryProvider, foldOptions, partitionFoldTime, foldLabel);
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

        internal abstract void Fold();

        public abstract void Dispose();
    }

    private sealed class PartitionFold<TKey, TPath>(IPbtStore store, byte zone,
        ArrayPoolList<PbtWriteOperation<TKey>> operations, ArrayPoolList<int> table,
        bool collectMetrics, IRefCountingMemoryProvider memoryProvider, ParallelOptions foldOptions,
        IMetricObserver? foldTime, StringLabel foldLabel) : PartitionFold(zone, collectMetrics)
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
            Span<byte> sourceBuffer = stackalloc byte[PbtTreeKey.MaxLength];
            GroupFrameReader<TKey, TPath> reader = new(store, 8, Current.Borrow(sourceBuffer).Hash(8, Metrics), Metrics);
            using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
            {
                using PbtNodeGroupWriter<TPath> writer = new(8, memoryProvider);
                TrieUpdater<TKey, TPath>.OwnedSubtree ownedCurrent = TrieUpdater<TKey, TPath>.OwnedSubtree.TakeFrom<PbtTreeKey, PbtStorageNodePath>(ref Current);
                TrieUpdater<TKey, TPath>.TraversalSubtree current = ownedCurrent.Borrow(sourceBuffer);
                TrieUpdater<TKey, TPath>.OwnedSubtree result = default;
                bool foldBucketsInParallel = foldOptions.MaxDegreeOfParallelism != 1 && !RuntimeInformation.IsSingleProcessor;
                TrieUpdater<TKey, TPath>.FoldContext context = new(store, memoryProvider, Metrics,
                    foldBucketsInParallel ? foldOptions : null, foldBucketsInParallel ? operations.UnsafeGetInternalArray() : null);
                // Consume the producer's nibble bounds before filtering deletes or comparing deeper key prefixes.
                result = TrieUpdater<TKey, TPath>.FoldBoundary(context, ref reader, writer, current,
                    operations.AsSpan(), ref path, 8, new(table.AsSpan(), 8, false));
                using (RefCountingMemory? payload = writer.Detach())
                    store.SetNodeGroup(path, result.Borrow(sourceBuffer).Hash(8, Metrics), payload);
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
