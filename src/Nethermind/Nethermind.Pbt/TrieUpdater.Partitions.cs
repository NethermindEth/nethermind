// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Core.Threading;
using Nethermind.Pbt.NodeBuffer;
using static Nethermind.Pbt.TrieUpdater<Nethermind.Pbt.PbtVariableTreeKey, Nethermind.Pbt.PbtStorageNodePath>;

namespace Nethermind.Pbt;

public static partial class TrieUpdater
{
    private static readonly StringLabel _accountFoldLabel = new("account");
    private static readonly StringLabel _storageFoldLabel = new("storage");
    private static readonly FoldPhaseLabels _accountPhaseLabels = new("account");
    private static readonly FoldPhaseLabels _storagePhaseLabels = new("storage");
    private static readonly FoldPhaseLabels _rootPhaseLabels = new("root");

    /// <summary>The root group slot the account and code zones share, both of whose keys are <see cref="PbtPath"/>s.</summary>
    private const int AccountSlot = Eip8297KeyDerivation.AccountZone >> ZoneSlotShift;
    private const int StorageSlot = Eip8297KeyDerivation.StorageZone >> ZoneSlotShift;
    private const int ZoneSlotShift = 8 - PbtFourLevelGroupGeometry.LevelsPerGroup;

    /// <summary>Folds disjoint partitions concurrently before merging their shared ancestors.</summary>
    /// <remarks>
    /// The zones' first bits split them over the root group's boundary slots: the account and code zones share one and
    /// the storage zone has its own, so below the root group every slot is an ordinary sorted fold of one key type.
    /// The touched root slots, their zones' shard sorts and the touched slots of every frame wide enough to fan out,
    /// merged into runs of at least the minimum <paramref name="fanOut"/> gives for the frame's subtree size, share
    /// <paramref name="foldQuota"/>: a fan-out folds its parts on the calling thread while no slot is free, and the first
    /// slot it takes admits a parallel loop over the parts still left, whose workers charge themselves as they start; a
    /// quota of one folds everything serially. Each root slot's fold time is observed on <paramref name="partitionFoldTime"/>,
    /// labelled <c>account</c> for the one the code zone shares, so an imbalance between them is visible.
    /// Each zone's shards are sorted in place.
    /// The supplied store must support concurrent reads and <see cref="IPbtStore.StartWriteBatch"/> calls. Failed
    /// folds may leave partial writes; the caller owns failure isolation and must not reuse that state without
    /// recovery.
    /// </remarks>
    /// <param name="store">The node-group store the fold reads from and publishes into.</param>
    /// <param name="currentRoot">The root of the tree the changes apply to, default for an empty tree, and its group's pointer, or 0 when the store does not hold it.</param>
    /// <param name="changes">The account, code and storage write batches to fold, each sharded by key nibble.</param>
    /// <param name="foldQuota">The worker slots the fold's parallel parts share.</param>
    /// <param name="fanOut">The fewest operations worth handing a worker, by the stored size below its buckets.</param>
    /// <param name="partitionFoldTime">Observes each root slot's fold time, labelled by partition; <c>null</c> to skip.</param>
    /// <param name="phaseTime">
    /// Observes each partition's sort and fold time, and every commit's time, labelled by partition (<c>root</c> for the root
    /// group's commit) and phase as <see cref="FoldPhaseLabels"/> names them; <c>null</c> to skip.
    /// </param>
    /// <returns>The root of the updated tree and its group's pointer, which is 0 for an empty tree.</returns>
    public static (ValueHash256 Hash, ulong Pointer) UpdateRoot(
        IPbtStore store,
        in (ValueHash256 Hash, ulong Pointer) currentRoot,
        PbtPartitionBatches changes,
        ConcurrencyController foldQuota,
        FoldFanOut fanOut,
        IMetricObserver? partitionFoldTime,
        IMetricObserver? phaseTime)
    {
        Debug.Assert(Eip8297KeyDerivation.CodeZone >> ZoneSlotShift == AccountSlot && StorageSlot != AccountSlot, "Only the account and code zones share a root slot.");
        using RootSlotFold<PbtPath, PbtNodePath> accountFold = new(store, foldQuota, fanOut, partitionFoldTime, _accountFoldLabel, phaseTime, _accountPhaseLabels);
        using RootSlotFold<PbtStoragePath, PbtStorageNodePath> storageFold = new(store, foldQuota, fanOut, partitionFoldTime, _storageFoldLabel, phaseTime, _storagePhaseLabels);
        accountFold.Add(changes.Account);
        accountFold.Add(changes.Code);
        storageFold.Add(changes.Storage);
        int touchedSlots = (accountFold.IsEmpty ? 0 : 1 << AccountSlot) | (storageFold.IsEmpty ? 0 : 1 << StorageSlot);
        if (touchedSlots == 0) return (currentRoot.Hash, PbtGroupRecordCodec.PersistentPointer(currentRoot.Pointer));

        ForeignSlotFold foldSlot = (int slot, in BoundaryNode boundary, long descendantBytes, Span<byte> encoding) => slot == AccountSlot
            ? accountFold.Fold(slot, boundary, descendantBytes, encoding)
            : storageFold.Fold(slot, boundary, descendantBytes, encoding);
        PbtTraversalPath rootPath = new(Span<byte>.Empty);
        FoldWorkingState workingState = FoldWorkingState.Rent();
        try
        {
            FoldContext context = new(store, foldQuota, null, fanOut, workingState, null, phaseTime, _rootPhaseLabels);
            // An empty tree owns no groups; a group left at the root path by an earlier tree is stale.
            if (currentRoot.Hash == default)
            {
                AbsentGroupFrame emptyRoot = new(0, 0, 0, 0);
                return FoldRoot(context, ref emptyRoot, default, touchedSlots, foldSlot);
            }
            if (!store.TryLocate(currentRoot.Pointer, out PbtGroupRecord rootRecord) && !workingState.Loaded.TryLoad(store, rootPath, out rootRecord))
                throw new InvalidDataException("A referenced PBT node group is missing.");
            GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> rootReader = new(rootRecord);
            return FoldRoot(context, ref rootReader, rootReader.TakeRoot(currentRoot.Hash), touchedSlots, foldSlot);
        }
        finally
        {
            FoldWorkingState.Return(workingState);
        }
    }

    /// <summary>Rebuilds the root group of <paramref name="rootReader"/> around its touched slots, each folded by <paramref name="foldSlot"/>, and publishes it.</summary>
    private static (ValueHash256 Hash, ulong Pointer) FoldRoot<TRoot>(FoldContext context, ref TRoot rootReader, in BoundaryNode root, int touchedSlots, ForeignSlotFold foldSlot)
        where TRoot : struct, IGroupFrame
    {
        ValueHash256 hash = default;
        // A fold that throws leaves its writer to the collector rather than to the working state.
        PbtNodeGroupWriter rootWriter = context.WorkingState.RentWriter(0);
        PbtTraversalPath rootPath = new(Span<byte>.Empty);
        StoredGroupHashes.Open(out StoredGroupHashes hashes);
        ComposedNode rootNode = WalkFrameOverForeignSlots(context, ref rootReader, ref hashes, rootWriter, root, rootPath, touchedSlots, foldSlot);
        if (!rootNode.IsEmpty)
        {
            ReadOnlySpan<byte> entry = rootWriter.Entry(rootNode.Offset, rootNode.Length).Span;
            rootWriter.ValidateEntry(rootPath, PbtFourLevelGroupGeometry.RootPosition, entry);
            hash = rootNode.Hash != default || PbtNodeCodec.IsLeaf(entry) ? rootNode.Hash : HashBranch(entry);
        }
        GroupPublication publication = PublishGroup(context, ref rootReader, rootWriter, rootPath);
        return (hash, context.Commit(publication.Pointer));
    }

    /// <summary>The labels of one partition's phases on the trie updater's phase observer: <c>sort</c>, <c>fold</c>, <c>commit_size</c> and <c>commit_write</c>.</summary>
    /// <remarks>The labels are stable, so an instance must live for the process.</remarks>
    public sealed class FoldPhaseLabels(string partition)
    {
        public IMetricLabels Sort { get; } = new PhaseLabel(partition, "sort");
        public IMetricLabels Fold { get; } = new PhaseLabel(partition, "fold");
        public IMetricLabels CommitSize { get; } = new PhaseLabel(partition, "commit_size");
        public IMetricLabels CommitWrite { get; } = new PhaseLabel(partition, "commit_write");

        private sealed class PhaseLabel(string partition, string phase) : IStableMetricLabels
        {
            public string[] Labels { get; } = [partition, phase];
        }
    }

    /// <summary>The operations under one root boundary slot, all of one key type, folded below the root group.</summary>
    private sealed class RootSlotFold<TKey, TPath>(IPbtStore store, ConcurrencyController foldQuota,
        FoldFanOut fanOut, IMetricObserver? foldTime, StringLabel foldLabel, IMetricObserver? phaseTime, FoldPhaseLabels phaseLabels) : IDisposable
        where TKey : unmanaged, IPbtKey<TKey>
        where TPath : struct, IPbtNodePath<TPath>
    {
        private ArrayPoolList<PbtWriteOperation<TKey>>? _operations;
        /// <summary>Each zone's operation range and the producer's shard table over it.</summary>
        private readonly List<(int Start, int Count, ArrayPoolList<int> Table)> _zones = [];

        public bool IsEmpty => _operations is null;

        /// <summary>Takes a zone's batch, whose keys all sort after those of the zones taken before.</summary>
        public void Add(PbtWriteBatch<TKey>? batch)
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

        public SlotNode Fold(int slot, in BoundaryNode boundary, long descendantBytes, Span<byte> encoding)
        {
            long start = Stopwatch.GetTimestamp();
            TrieUpdater<TKey, TPath>.FoldWorkingState workingState = TrieUpdater<TKey, TPath>.FoldWorkingState.Rent();
            using TrieUpdater<TKey, TPath>.SpareWorkingStates spares = new();
            try
            {
                TrieUpdater<TKey, TPath>.FoldContext context = new(store, foldQuota, _operations!.UnsafeGetInternalArray(), fanOut, workingState, spares, phaseTime, phaseLabels);
                Span<PbtWriteOperation<TKey>> operations = _operations.AsSpan();
                // The producer grouped each zone by its shard nibble, so sorted shards sort the zone; each shard's leaves are
                // hashed with its sort, so the fold reads leaf hashes in place of values.
                foreach ((int zoneStart, int count, ArrayPoolList<int> table) in _zones)
                    TrieUpdater<TKey, TPath>.SortShards(context, operations.Slice(zoneStart, count), table.AsSpan());
                TrieUpdater<TKey, TPath>.AssertSorted(operations);
                long sorted = phaseTime is null ? 0 : Stopwatch.GetTimestamp();
                SlotNode result = TrieUpdater<TKey, TPath>.FoldDetachedSlot(context, TPath.Create(ReadOnlySpan<byte>.Empty, 0), 0, slot,
                    TrieUpdater<TKey, TPath>.BoundaryNode.TakeFrom<PbtVariableTreeKey, PbtStorageNodePath>(boundary), descendantBytes, operations, encoding);
                long folded = Stopwatch.GetTimestamp();
                foldTime?.Observe(folded - start, foldLabel);
                if (phaseTime is not null)
                {
                    phaseTime.Observe(sorted - start, phaseLabels.Sort);
                    phaseTime.Observe(folded - sorted, phaseLabels.Fold);
                }
                return result;
            }
            finally
            {
                TrieUpdater<TKey, TPath>.FoldWorkingState.Return(workingState);
            }
        }

        public void Dispose()
        {
            _operations?.Dispose();
            foreach ((_, _, ArrayPoolList<int> table) in _zones) table.Dispose();
        }
    }
}
