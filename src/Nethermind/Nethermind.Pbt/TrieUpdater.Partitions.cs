// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using static Nethermind.Pbt.TrieUpdater;
using static Nethermind.Pbt.TrieUpdater<Nethermind.Pbt.PbtStorageFullKey, Nethermind.Pbt.PbtStorageNodePath>;

namespace Nethermind.Pbt;

public static partial class TrieUpdater
{
    private static readonly PbtStorageNodePath RootPath = new([], 0);

    /// <summary>Applies an account/code key batch to a tree containing only small keys.</summary>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<PbtFullKey> changes) =>
        TrieUpdater<PbtFullKey, PbtNodePath>.UpdateRoot(store, currentRoot, changes);

    /// <summary>Applies a storage-capable key batch to a complete tree.</summary>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<PbtStorageFullKey> changes) =>
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.UpdateRoot(store, currentRoot, changes);

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<PbtFullKey> changes,
        TrieUpdaterMetrics? metrics, IRefCountingMemoryProvider? memoryProvider = null) =>
        TrieUpdater<PbtFullKey, PbtNodePath>.UpdateRoot(store, currentRoot, changes, metrics, memoryProvider);

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<PbtStorageFullKey> changes,
        TrieUpdaterMetrics? metrics, IRefCountingMemoryProvider? memoryProvider = null) =>
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.UpdateRoot(store, currentRoot, changes, metrics, memoryProvider);

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatchSet<PbtFullKey> changes,
        TrieUpdaterMetrics? metrics = null, IRefCountingMemoryProvider? memoryProvider = null) =>
        TrieUpdater<PbtFullKey, PbtNodePath>.UpdateRoot(store, currentRoot, changes, metrics, memoryProvider);

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatchSet<PbtStorageFullKey> changes,
        TrieUpdaterMetrics? metrics = null, IRefCountingMemoryProvider? memoryProvider = null) =>
        TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.UpdateRoot(store, currentRoot, changes, metrics, memoryProvider);

    /// <summary>Folds disjoint partitions concurrently before merging their shared ancestors.</summary>
    /// <remarks>
    /// The supplied store must support concurrent reads and writes. Failed folds may leave partial writes;
    /// the caller owns failure isolation and must not reuse that state without recovery.
    /// </remarks>
    internal static ValueHash256 UpdateRoot(
        IPbtStore store,
        in ValueHash256 currentRoot,
        PbtPartitionBatches changes,
        TrieUpdaterMetrics? metrics = null,
        IRefCountingMemoryProvider? memoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        using ArrayPoolList<PartitionFold> workers = new(3);
        using ArrayPoolListRef<GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath>> sharedReaders = new(16, 16);
        using ArrayPoolListRef<PbtNodeGroupWriter?> sharedWriters = new(16, 16);
        int initializedReaders = 0;
        memoryProvider ??= PooledRefCountingMemoryProvider.Instance;
        using ArrayPoolListRef<ArrayPoolList<Subtree>?> zoneBoundaries = new(16, 16);
        using ArrayPoolListRef<Subtree> rootBoundaries = new(16, 16);
        Span<int> touchedZoneMasks = stackalloc int[16];
        touchedZoneMasks.Clear();
        try
        {
            AddWorker<PbtFullKey, PbtNodePath>(changes.Account, Eip8297KeyDerivation.AccountZone);
            AddWorker<PbtFullKey, PbtNodePath>(changes.Code, Eip8297KeyDerivation.CodeZone);
            AddWorker<PbtStorageFullKey, PbtStorageNodePath>(changes.Storage, Eip8297KeyDerivation.StorageZone);
            if (workers.Count == 0) return currentRoot;

            GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> rootReader = new(store, RootPath, metrics);
            try
            {
                using PbtNodeGroupWriter rootWriter = new(RootPath, memoryProvider);
                int touchedRootMask = 0;
                foreach (PartitionFold worker in workers)
                {
                    touchedRootMask |= 1 << (worker.Zone >> 4);
                    touchedZoneMasks[worker.Zone >> 4] |= 1 << (worker.Zone & 15);
                }
                Subtree root = rootReader.Take(rootWriter, RootPath, allowAbsent: true);
                try { Decompose(ref rootReader, rootWriter, ref root, 0, rootBoundaries.AsSpan()); }
                finally { root.Dispose(); }
                foreach (PartitionFold worker in workers)
                {
                    int slot = worker.Zone >> 4;
                    ref GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> sharedReader = ref sharedReaders.AsSpan()[slot];
                    if (sharedWriters[slot] is not { } sharedWriter)
                    {
                        sharedReader = new(store, new PbtStorageNodePath([(byte)(slot << 4)], 4), metrics);
                        initializedReaders |= 1 << slot;
                        sharedWriter = new(sharedReader.GroupKey, memoryProvider);
                        sharedWriters[slot] = sharedWriter;
                        zoneBoundaries[slot] = new(16, 16);
                        rootReader.Resolve(rootWriter, ref rootBoundaries.AsSpan()[slot]);
                        Decompose(ref sharedReader, sharedWriter, ref rootBoundaries.AsSpan()[slot], 4, zoneBoundaries[slot]!.AsSpan());
                    }
                    sharedReader.Resolve(sharedWriter, ref zoneBoundaries[slot]!.AsSpan()[worker.Zone & 15]);
                    worker.Current = Subtree.Move(ref zoneBoundaries[slot]!.AsSpan()[worker.Zone & 15]);
                }

                Parallel.ForEach(workers, new ParallelOptions { MaxDegreeOfParallelism = 3 }, static worker => worker.Fold());

                foreach (PartitionFold worker in workers)
                {
                    zoneBoundaries[worker.Zone >> 4]![worker.Zone & 15] = Subtree.Move(ref worker.Result);
                    if (worker.Metrics is { } workerMetrics) metrics!.Add(workerMetrics);
                }
                for (int slot = 0; slot < sharedReaders.Count; slot++)
                {
                    if (sharedWriters[slot] is not { } sharedWriter) continue;
                    ref GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> sharedReader = ref sharedReaders.AsSpan()[slot];
                    rootBoundaries[slot] = Compose(ref sharedReader, sharedWriter, metrics, zoneBoundaries[slot]!.AsSpan(), touchedZoneMasks[slot]);
                    sharedWriter.Flush(store, metrics, ref sharedReader);
                }
                Subtree result = Compose(ref rootReader, rootWriter, metrics, rootBoundaries.AsSpan(), touchedRootMask);
                try
                {
                    ValueHash256 hash = rootWriter.Write(ref rootReader, PbtFourLevelGroupGeometry.RootPosition, 0, ref result);
                    rootWriter.Flush(store, metrics, ref rootReader);
                    return hash;
                }
                finally { result.Dispose(); }
            }
            finally { rootReader.Dispose(); }
        }
        finally
        {
            foreach (PartitionFold worker in workers) worker.Dispose();
            Dispose(rootBoundaries.AsSpan());
            for (int slot = 0; slot < sharedReaders.Count; slot++)
            {
                sharedWriters[slot]?.Dispose();
                if ((initializedReaders & (1 << slot)) != 0) sharedReaders.AsSpan()[slot].Dispose();
            }
            foreach (ArrayPoolList<Subtree>? boundaries in zoneBoundaries)
            {
                if (boundaries is null) continue;
                Dispose(boundaries.AsSpan());
                boundaries.Dispose();
            }
        }

        void AddWorker<TKey, TPath>(PbtWriteBatch<TKey>? batch, byte zone)
            where TKey : struct, IPbtKey<TKey>
            where TPath : class, IPbtNodePath<TPath>
        {
            if (batch is null) return;
            ArgumentOutOfRangeException.ThrowIfNotEqual(batch.ShardNibbleIndex, 2);
            batch.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table);
            PartitionFold<TKey, TPath> worker = new(store, zone, operations, table, metrics is not null, memoryProvider);
            if (operations.Count != 0) workers.Add(worker);
            else worker.Dispose();
        }
    }

    private abstract class PartitionFold(byte zone, bool collectMetrics) : IDisposable
    {
        internal byte Zone { get; } = zone;
        internal TrieUpdaterMetrics? Metrics { get; } = collectMetrics ? new() : null;
        internal Subtree Current;
        internal Subtree Result;

        internal abstract void Fold();

        public virtual void Dispose()
        {
            Current.Dispose();
            Result.Dispose();
        }
    }

    private sealed class PartitionFold<TKey, TPath>(IPbtStore store, byte zone,
        ArrayPoolList<PbtWriteOperation<TKey>> operations, ArrayPoolList<int> table,
        bool collectMetrics, IRefCountingMemoryProvider memoryProvider) : PartitionFold(zone, collectMetrics)
        where TKey : struct, IPbtKey<TKey>
        where TPath : class, IPbtNodePath<TPath>
    {
        internal override void Fold()
        {
            GroupFrameReader<TKey, TPath> reader = new(store, TPath.Create([Zone], 8), Metrics);
            try
            {
                using PbtNodeGroupWriter writer = new(reader.GroupKey, memoryProvider);
                TrieUpdater<TKey, TPath>.Subtree current = TrieUpdater<TKey, TPath>.Subtree.TakeFrom<PbtStorageFullKey, PbtStorageNodePath>(ref Current);
                TrieUpdater<TKey, TPath>.Subtree result = default;
                try
                {
                    // Consume the producer's nibble bounds before filtering deletes or comparing deeper key prefixes.
                    result = TrieUpdater<TKey, TPath>.FoldBoundary(store, Metrics, ref reader, writer, memoryProvider, ref current,
                        operations.AsSpan(), 8, new(table.AsSpan(), 8, false));
                    writer.Flush(store, Metrics, ref reader);
                    Result = Subtree.TakeFrom<TKey, TPath>(ref result);
                }
                finally
                {
                    current.Dispose();
                    result.Dispose();
                }
            }
            finally { reader.Dispose(); }
        }

        public override void Dispose()
        {
            base.Dispose();
            operations.Dispose();
            table.Dispose();
        }
    }
}

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : struct, IPbtKey<TKey>
    where TPath : class, IPbtNodePath<TPath>
{
    internal static Subtree FoldBoundary(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        ref GroupFrameReader<TKey, TPath> reader,
        PbtNodeGroupWriter writer,
        IRefCountingMemoryProvider memoryProvider,
        ref Subtree current,
        Span<PbtWriteOperation<TKey>> operations,
        int bitDepth,
        BucketPlan plan)
    {
        Span<byte> buffer = stackalloc byte[plan.GetBufferSize(operations.Length, bitDepth)];
        PartitionOutcome partition = plan.WithBuffer(buffer).BucketSort(operations, bitDepth, metrics);
        return FoldBoundaryFromPartition(store, metrics, ref reader, writer, memoryProvider, ref current, operations, bitDepth, partition);
    }
}
