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
        using ArrayPoolListRef<PbtNodeGroupWriter<PbtStorageNodePath>?> sharedWriters = new(16, 16);
        using (new GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath>.Scope(sharedReaders.AsSpan()))
        {
            memoryProvider ??= PooledRefCountingMemoryProvider.Instance;
            using ArrayPoolListRef<ArrayPoolList<DecompositionEntry>?> zoneBoundaries = new(16, 16);
            using ArrayPoolListRef<DecompositionEntry> rootBoundaries = new(PbtFourLevelGroupGeometry.BoundarySlots, PbtFourLevelGroupGeometry.BoundarySlots);
            uint rootFrontierMask = 0;
            Span<uint> zoneFrontierMasks = stackalloc uint[16];
            zoneFrontierMasks.Clear();
            Span<int> touchedZoneMasks = stackalloc int[16];
            touchedZoneMasks.Clear();
            try
            {
                AddWorker<PbtFullKey, PbtNodePath>(changes.Account, Eip8297KeyDerivation.AccountZone);
                AddWorker<PbtFullKey, PbtNodePath>(changes.Code, Eip8297KeyDerivation.CodeZone);
                AddWorker<PbtStorageFullKey, PbtStorageNodePath>(changes.Storage, Eip8297KeyDerivation.StorageZone);
                if (workers.Count == 0) return currentRoot;

                GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> rootReader = new(store, RootPath, currentRoot, metrics);
                using (new GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath>.Scope(ref rootReader))
                {
                    using PbtNodeGroupWriter<PbtStorageNodePath> rootWriter = new(RootPath, memoryProvider);
                    int touchedRootMask = 0;
                    foreach (PartitionFold worker in workers)
                    {
                        touchedRootMask |= 1 << (worker.Zone >> 4);
                        touchedZoneMasks[worker.Zone >> 4] |= 1 << (worker.Zone & 15);
                    }
                    Subtree root = rootReader.Take(rootWriter, PbtFourLevelGroupGeometry.RootPosition, allowAbsent: true);
                    Decompose(ref rootReader, rootWriter, ref root, 0, rootBoundaries.AsSpan(), ref rootFrontierMask, touchedRootMask);
                    foreach (PartitionFold worker in workers)
                    {
                        int slot = worker.Zone >> 4;
                        ref GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> sharedReader = ref sharedReaders.AsSpan()[slot];
                        if (sharedWriters[slot] is not { } sharedWriter)
                        {
                            Subtree boundary = TakeBoundary(ref rootReader, rootWriter, rootBoundaries.AsSpan(), ref rootFrontierMask, slot);
                            sharedReader = new(store, new PbtStorageNodePath([(byte)(slot << 4)], 4), boundary.Hash(4), metrics);
                            sharedWriter = new(sharedReader.GroupKey, memoryProvider);
                            sharedWriters[slot] = sharedWriter;
                            zoneBoundaries[slot] = new(PbtFourLevelGroupGeometry.BoundarySlots, PbtFourLevelGroupGeometry.BoundarySlots);
                            Decompose(ref sharedReader, sharedWriter, ref boundary, 4, zoneBoundaries[slot]!.AsSpan(), ref zoneFrontierMasks[slot], touchedZoneMasks[slot]);
                        }
                        worker.Current = TakeBoundary(ref sharedReader, sharedWriter, zoneBoundaries[slot]!.AsSpan(), ref zoneFrontierMasks[slot], worker.Zone & 15);
                    }

                    Parallel.ForEach(workers, new ParallelOptions { MaxDegreeOfParallelism = 3 }, static worker => worker.Fold());

                    foreach (PartitionFold worker in workers)
                    {
                        SetBoundary(zoneBoundaries[worker.Zone >> 4]!.AsSpan(), ref zoneFrontierMasks[worker.Zone >> 4], worker.Zone & 15, ref worker.Result);
                        if (worker.Metrics is { } workerMetrics) metrics!.Add(workerMetrics);
                    }
                    Span<byte> sharedPathBuffer = stackalloc byte[PbtStorageFullKey.MaxLength];
                    for (int slot = 0; slot < sharedReaders.Count; slot++)
                    {
                        if (sharedWriters[slot] is not { } sharedWriter) continue;
                        ref GroupFrameReader<PbtStorageFullKey, PbtStorageNodePath> sharedReader = ref sharedReaders.AsSpan()[slot];
                        Subtree composed = Compose(ref sharedReader, sharedWriter, metrics, zoneBoundaries[slot]!.AsSpan(), zoneFrontierMasks[slot]);
                        ValueHash256 groupHash = composed.Hash(4);
                        SetBoundary(rootBoundaries.AsSpan(), ref rootFrontierMask, slot, ref composed);
                        using RefCountingMemory? payload = sharedWriter.Detach();
                        PbtTraversalPath sharedPath = PbtTraversalPath.FromPath(sharedPathBuffer, sharedReader.GroupKey);
                        store.SetNodeGroup(sharedPath, groupHash, payload);
                    }
                    Subtree result = Compose(ref rootReader, rootWriter, metrics, rootBoundaries.AsSpan(), rootFrontierMask);
                    ValueHash256 hash = rootWriter.Write(PbtFourLevelGroupGeometry.RootPosition, 0, ref result);
                    PbtTraversalPath rootPath = new(Span<byte>.Empty);
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
                foreach (ArrayPoolList<DecompositionEntry>? boundaries in zoneBoundaries)
                {
                    if (boundaries is null) continue;
                    boundaries.Dispose();
                }
            }

            void AddWorker<TKey, TPath>(PbtWriteBatch<TKey>? batch, byte zone)
                where TKey : struct, IPbtKey<TKey>
                where TPath : struct, IPbtNodePath<TPath>
            {
                if (batch is null) return;
                ArgumentOutOfRangeException.ThrowIfNotEqual(batch.ShardNibbleIndex, 2);
                batch.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table);
                PartitionFold<TKey, TPath> worker = new(store, zone, operations, table, metrics is not null, memoryProvider);
                if (operations.Count != 0) workers.Add(worker);
                else worker.Dispose();
            }
        }
    }

    private abstract class PartitionFold(byte zone, bool collectMetrics) : IDisposable
    {
        internal byte Zone { get; } = zone;
        internal TrieUpdaterMetrics? Metrics { get; } = collectMetrics ? new() : null;
        internal Subtree Current;
        internal Subtree Result;

        internal abstract void Fold();

        public abstract void Dispose();
    }

    private sealed class PartitionFold<TKey, TPath>(IPbtStore store, byte zone,
        ArrayPoolList<PbtWriteOperation<TKey>> operations, ArrayPoolList<int> table,
        bool collectMetrics, IRefCountingMemoryProvider memoryProvider) : PartitionFold(zone, collectMetrics)
        where TKey : struct, IPbtKey<TKey>
        where TPath : struct, IPbtNodePath<TPath>
    {
        internal override void Fold()
        {
            Span<byte> pathBuffer = stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)];
            PbtTraversalPath path = new(pathBuffer);
            path.AppendMut(Zone >> 4);
            path.AppendMut(Zone & 15);
            GroupFrameReader<TKey, TPath> reader = new(store, path.ToPath<TPath>(), Current.Hash(8), Metrics);
            using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
            {
                using PbtNodeGroupWriter<TPath> writer = new(reader.GroupKey, memoryProvider);
                TrieUpdater<TKey, TPath>.Subtree current = TrieUpdater<TKey, TPath>.Subtree.TakeFrom<PbtStorageFullKey, PbtStorageNodePath>(ref Current);
                TrieUpdater<TKey, TPath>.Subtree result = default;
                // Consume the producer's nibble bounds before filtering deletes or comparing deeper key prefixes.
                result = TrieUpdater<TKey, TPath>.FoldBoundary(store, Metrics, ref reader, writer, memoryProvider, ref current,
                    operations.AsSpan(), ref path, 8, new(table.AsSpan(), 8, false));
                using (RefCountingMemory? payload = writer.Detach())
                    store.SetNodeGroup(path, result.Hash(8), payload);
                result = result.Materialize();
                Result = Subtree.TakeFrom<TKey, TPath>(ref result);
            }
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
    internal static Subtree FoldBoundary(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        ref GroupFrameReader<TKey, TPath> reader,
        PbtNodeGroupWriter<TPath> writer,
        IRefCountingMemoryProvider memoryProvider,
        ref Subtree current,
        Span<PbtWriteOperation<TKey>> operations,
        ref PbtTraversalPath path,
        int bitDepth,
        BucketPlan plan)
    {
        Span<byte> buffer = stackalloc byte[plan.GetBufferSize(operations.Length, bitDepth)];
        PartitionOutcome partition = plan.WithBuffer(buffer).BucketSort(operations, bitDepth, metrics);
        return FoldBoundaryFromPartition(store, metrics, ref reader, writer, memoryProvider, ref current, operations, ref path, bitDepth, partition);
    }
}
