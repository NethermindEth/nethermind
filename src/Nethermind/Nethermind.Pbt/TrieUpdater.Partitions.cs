// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Core.Metric;
using Nethermind.Core.Threading;
using static Nethermind.Pbt.TrieUpdater<Nethermind.Pbt.PbtStorageTreeKey, Nethermind.Pbt.PbtStorageNodePath>;

namespace Nethermind.Pbt;

public static partial class TrieUpdater
{
    private static readonly StringLabel _accountFoldLabel = new("account");
    private static readonly StringLabel _storageFoldLabel = new("storage");

    /// <summary>The root group slot the account and code zones share, both of whose keys are <see cref="PbtPath"/>s.</summary>
    private const int AccountSlot = Eip8297KeyDerivation.AccountZone >> 4;
    private const int StorageSlot = Eip8297KeyDerivation.StorageZone >> 4;

    /// <summary>Folds disjoint partitions concurrently before merging their shared ancestors.</summary>
    /// <remarks>
    /// The zones' first nibbles split them over the root group's boundary slots: the account and code zones share one and
    /// the storage zone has its own, so below the root group every slot is an ordinary sorted fold of one key type.
    /// The touched root slots, their zones' shard sorts and the touched slots of every frame wide enough to fan out,
    /// merged into runs of at least the minimum <paramref name="fanOut"/> gives for the frame's subtree size, share
    /// <paramref name="foldQuota"/>: a fan-out folds its parts on the calling thread while no slot is free, and the first
    /// slot it takes admits a parallel loop over the parts still left, whose workers charge themselves as they start; a
    /// quota of one folds everything serially. Each root slot's fold time is observed on <paramref name="partitionFoldTime"/>,
    /// labelled <c>account</c> for the one the code zone shares, so an imbalance between them is visible.
    /// Each zone's shards are sorted in place.
    /// The supplied store must support concurrent reads; each worker writes through its own <see cref="IPbtStore.CreateWriter"/>. Failed
    /// folds may leave partial writes; the caller owns failure isolation and must not reuse that state without
    /// recovery.
    /// </remarks>
    /// <param name="store">The node-group store the fold reads from and publishes into.</param>
    /// <param name="currentRoot">The root of the tree the changes apply to; default for an empty tree.</param>
    /// <param name="changes">The account, code and storage write batches to fold, each sharded by key nibble.</param>
    /// <param name="foldQuota">The worker slots the fold's parallel parts share.</param>
    /// <param name="fanOut">The fewest operations worth handing a worker, by the stored size below its buckets.</param>
    /// <param name="partitionFoldTime">Observes each root slot's fold time, labelled by partition; <c>null</c> to skip.</param>
    /// <param name="memoryProvider">Rents the published groups' memory; <c>null</c> for the default pool.</param>
    /// <returns>The root of the updated tree.</returns>
    public static ValueHash256 UpdateRoot(
        IPbtStore store,
        in ValueHash256 currentRoot,
        PbtPartitionBatches changes,
        ConcurrencyController foldQuota,
        FoldFanOut fanOut,
        IMetricObserver? partitionFoldTime,
        IRefCountingMemoryProvider? memoryProvider = null)
    {
        Debug.Assert(Eip8297KeyDerivation.CodeZone >> 4 == AccountSlot && StorageSlot != AccountSlot, "Only the account and code zones share a root slot.");
        memoryProvider ??= PooledRefCountingMemoryProvider.Instance;
        using RootSlotFold<PbtPath, PbtNodePath> accountFold = new(store, memoryProvider, foldQuota, fanOut, partitionFoldTime, _accountFoldLabel);
        using RootSlotFold<PbtStoragePath, PbtStorageNodePath> storageFold = new(store, memoryProvider, foldQuota, fanOut, partitionFoldTime, _storageFoldLabel);
        accountFold.Add(changes.Account);
        accountFold.Add(changes.Code);
        storageFold.Add(changes.Storage);
        int touchedSlots = (accountFold.IsEmpty ? 0 : 1 << AccountSlot) | (storageFold.IsEmpty ? 0 : 1 << StorageSlot);
        if (touchedSlots == 0) return currentRoot;

        ForeignSlotFold foldSlot = (int slot, in BoundaryNode boundary, long descendantBytes, Span<byte> encoding) => slot == AccountSlot
            ? accountFold.Fold(slot, boundary, descendantBytes, encoding)
            : storageFold.Fold(slot, boundary, descendantBytes, encoding);
        PbtTraversalPath rootPath = new(Span<byte>.Empty);
        if (!GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.TryLoad(store, rootPath, currentRoot,
                out GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> rootReader))
        {
            if (currentRoot != default) throw new InvalidDataException("A referenced PBT node group is missing.");
            AbsentGroupFrame<PbtStorageTreeKey, PbtStorageNodePath> emptyRoot = new(0);
            return FoldRoot(store, ref emptyRoot, default, touchedSlots, foldSlot, foldQuota, fanOut, memoryProvider);
        }
        using (new GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath>.Scope(ref rootReader))
            return FoldRoot(store, ref rootReader, rootReader.TakeRoot(), touchedSlots, foldSlot, foldQuota, fanOut, memoryProvider);
    }

    /// <summary>Rebuilds the root group of <paramref name="rootReader"/> around its touched slots, each folded by <paramref name="foldSlot"/>, and publishes it.</summary>
    private static ValueHash256 FoldRoot<TRoot>(IPbtStore store, ref TRoot rootReader, in BoundaryNode root, int touchedSlots, ForeignSlotFold foldSlot,
        ConcurrencyController foldQuota, FoldFanOut fanOut, IRefCountingMemoryProvider memoryProvider)
        where TRoot : struct, IGroupFrame<PbtStorageTreeKey, PbtStorageNodePath>
    {
        using IPbtConcurrentWriter storeWriter = store.CreateWriter();
        using PbtNodeGroupWriter<PbtStorageNodePath> rootWriter = PbtNodeGroupWriter<PbtStorageNodePath>.Rent(0, memoryProvider);
        PbtTraversalPath rootPath = new(Span<byte>.Empty);
        StoredGroupHashes.Open(out StoredGroupHashes hashes);
        FoldContext context = new(store, storeWriter, memoryProvider, foldQuota, null, fanOut);
        ComposedNode rootNode = WalkFrameOverForeignSlots(context, ref rootReader, ref hashes, rootWriter, root, rootPath, touchedSlots, foldSlot);
        FoldResult rootResult = default;
        TakeRoot(rootWriter, rootPath, 0, rootNode, ref rootResult);
        ValueHash256 hash = rootWriter.WriteRoot(rootPath, rootResult);
        PublishGroup(storeWriter, ref rootReader, rootWriter, rootPath, hash);
        return hash;
    }

    /// <summary>The operations under one root boundary slot, all of one key type, folded below the root group.</summary>
    private sealed class RootSlotFold<TKey, TPath>(IPbtStore store, IRefCountingMemoryProvider memoryProvider, ConcurrencyController foldQuota,
        FoldFanOut fanOut, IMetricObserver? foldTime, StringLabel foldLabel) : IDisposable
        where TKey : unmanaged, IPbtKey<TKey>
        where TPath : struct, IPbtNodePath<TPath>
    {
        private ArrayPoolList<PbtWriteOperation<TKey>>? _operations;
        /// <summary>Each zone's operation range and the producer's shard table over it.</summary>
        private readonly List<(int Start, int Count, ArrayPoolList<int> Table)> _zones = [];

        internal bool IsEmpty => _operations is null;

        /// <summary>Takes a zone's batch, whose keys all sort after those of the zones taken before.</summary>
        internal void Add(PbtWriteBatch<TKey>? batch)
        {
            if (batch is null) return;
            batch.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table);
            if (operations.Count == 0)
            {
                operations.Dispose();
                table.Dispose();
                return;
            }
            _zones.Add((_operations?.Count ?? 0, operations.Count, table));
            if (_operations is null)
            {
                _operations = operations;
                return;
            }
            _operations.AddRange(operations.AsSpan());
            operations.Dispose();
        }

        internal SlotNode Fold(int slot, in BoundaryNode boundary, long descendantBytes, Span<byte> encoding)
        {
            long start = Stopwatch.GetTimestamp();
            using IPbtConcurrentWriter writer = store.CreateWriter();
            TrieUpdater<TKey, TPath>.FoldContext context = new(store, writer, memoryProvider, foldQuota, _operations!.UnsafeGetInternalArray(), fanOut);
            Span<PbtWriteOperation<TKey>> operations = _operations.AsSpan();
            // The producer grouped each zone by its shard nibble, so sorted shards sort the zone; each shard's leaves are
            // hashed with its sort, so the fold reads leaf hashes in place of values.
            foreach ((int zoneStart, int count, ArrayPoolList<int> table) in _zones)
                TrieUpdater<TKey, TPath>.SortShards(context, operations.Slice(zoneStart, count), table.AsSpan());
            TrieUpdater<TKey, TPath>.AssertSorted(operations);
            TrieUpdater<TKey, TPath>.SlotNode result = TrieUpdater<TKey, TPath>.FoldDetachedSlot(context, TPath.Create(ReadOnlySpan<byte>.Empty, 0), 0, slot,
                TrieUpdater<TKey, TPath>.BoundaryNode.TakeFrom<PbtStorageTreeKey, PbtStorageNodePath>(boundary), descendantBytes, operations, encoding);
            foldTime?.Observe(Stopwatch.GetTimestamp() - start, foldLabel);
            return new(result.Length, result.Hash) { SizeDelta = result.SizeDelta };
        }

        public void Dispose()
        {
            _operations?.Dispose();
            foreach ((_, _, ArrayPoolList<int> table) in _zones) table.Dispose();
        }
    }
}
