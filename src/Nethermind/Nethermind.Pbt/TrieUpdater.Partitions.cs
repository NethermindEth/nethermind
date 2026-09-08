// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
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
        using ArrayPoolListRef<GroupMutationFrame?> sharedGroups = new(16, 16);
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

            using GroupMutationFrame rootGroup = new(store, RootPath, metrics, memoryProvider);
            int touchedRootMask = 0;
            foreach (PartitionFold worker in workers)
            {
                touchedRootMask |= 1 << (worker.Zone >> 4);
                touchedZoneMasks[worker.Zone >> 4] |= 1 << (worker.Zone & 15);
            }
            Subtree root = rootGroup.Take(RootPath, allowAbsent: true);
            try { Decompose(rootGroup, ref root, 0, rootBoundaries.AsSpan()); }
            finally { root.Dispose(); }
            foreach (PartitionFold worker in workers)
            {
                int slot = worker.Zone >> 4;
                if (sharedGroups[slot] is not { } sharedGroup)
                {
                    sharedGroup = new(store, new PbtStorageNodePath([(byte)(slot << 4)], 4), metrics, memoryProvider);
                    sharedGroups[slot] = sharedGroup;
                    zoneBoundaries[slot] = new(16, 16);
                    rootGroup.Resolve(ref rootBoundaries.AsSpan()[slot]);
                    Decompose(sharedGroup, ref rootBoundaries.AsSpan()[slot], 4, zoneBoundaries[slot]!.AsSpan());
                }
                sharedGroup.Resolve(ref zoneBoundaries[slot]!.AsSpan()[worker.Zone & 15]);
                worker.Current = Subtree.Move(ref zoneBoundaries[slot]!.AsSpan()[worker.Zone & 15]);
            }

            Parallel.ForEach(workers, new ParallelOptions { MaxDegreeOfParallelism = 3 }, static worker => worker.Fold());

            foreach (PartitionFold worker in workers)
            {
                zoneBoundaries[worker.Zone >> 4]![worker.Zone & 15] = Subtree.Move(ref worker.Result);
                if (worker.Metrics is { } workerMetrics) metrics!.Add(workerMetrics);
            }
            for (int slot = 0; slot < sharedGroups.Count; slot++)
            {
                if (sharedGroups[slot] is not { } sharedGroup) continue;
                rootBoundaries[slot] = Compose(sharedGroup, zoneBoundaries[slot]!.AsSpan(), touchedZoneMasks[slot]);
                sharedGroup.Flush();
            }
            Subtree result = Compose(rootGroup, rootBoundaries.AsSpan(), touchedRootMask);
            try
            {
                ValueHash256 hash = rootGroup.Write(PbtFourLevelGroupGeometry.RootPosition, 0, ref result);
                rootGroup.Flush();
                return hash;
            }
            finally { result.Dispose(); }
        }
        finally
        {
            foreach (PartitionFold worker in workers) worker.Dispose();
            Dispose(rootBoundaries.AsSpan());
            foreach (GroupMutationFrame? sharedGroup in sharedGroups) sharedGroup?.Dispose();
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
        bool collectMetrics, IRefCountingMemoryProvider? memoryProvider) : PartitionFold(zone, collectMetrics)
        where TKey : struct, IPbtKey<TKey>
        where TPath : class, IPbtNodePath<TPath>
    {
        internal override void Fold()
        {
            using TrieUpdater<TKey, TPath>.GroupMutationFrame group = new(store, TPath.Create([Zone], 8), Metrics, memoryProvider);
            TrieUpdater<TKey, TPath>.Subtree current = TrieUpdater<TKey, TPath>.Subtree.TakeFrom<PbtStorageFullKey, PbtStorageNodePath>(ref Current);
            TrieUpdater<TKey, TPath>.Subtree result = default;
            try
            {
                // Consume the producer's nibble bounds before filtering deletes or comparing deeper key prefixes.
                result = TrieUpdater<TKey, TPath>.FoldBoundary(store, Metrics, group, ref current,
                    operations.AsSpan(), new(table.AsSpan(), 8, 8, false, false));
                group.Flush();
                Result = Subtree.TakeFrom<TKey, TPath>(ref result);
            }
            finally
            {
                current.Dispose();
                result.Dispose();
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            operations.Dispose();
            table.Dispose();
        }
    }
}
