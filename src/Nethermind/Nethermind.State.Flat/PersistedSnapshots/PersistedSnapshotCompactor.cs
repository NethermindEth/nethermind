// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.Storage;
using Prometheus;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Manages conversion of in-memory snapshots to persisted snapshots (HSST files)
/// and compaction of persisted snapshots. Mirrors <see cref="SnapshotCompactor"/>'s
/// logarithmic compaction strategy for the persisted layer.
/// </summary>
public class PersistedSnapshotCompactor(
    IPersistedSnapshotRepository persistedSnapshotRepository,
    IArenaManager arenaManager,
    IFlatDbConfig config,
    ILogManager logManager) : IPersistedSnapshotCompactor
{
    private readonly ILogger _logger = logManager.GetClassLogger<PersistedSnapshotCompactor>();
    private readonly int _compactSize = config.CompactSize;
    private readonly int _persistedSnapshotMaxCompactSize = config.PersistedSnapshotMaxCompactSize;
    private readonly int _minCompactSize = Math.Max(config.MinCompactSize, 2);
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
    private readonly long _maxCompactedSourceBytes = config.PersistedSnapshotMaxCompactedSourceBytes;

    /// <summary>
    /// Try to compact persisted snapshots using logarithmic compaction.
    /// Mirrors <see cref="SnapshotCompactor.GetSnapshotsToCompact"/> logic.
    /// Skips compactSize == _compactSize since persistable snapshots are now produced
    /// directly by PersistenceManager from in-memory compacted snapshots.
    /// </summary>
    public void DoCompactSnapshot(StateId snapshotTo)
    {
        if (_compactSize <= 0) return;

        long blockNumber = snapshotTo.BlockNumber;
        if (blockNumber == 0) return;

        int compactSize = (int)Math.Min(blockNumber & -blockNumber, _persistedSnapshotMaxCompactSize);
        if (compactSize < _minCompactSize) return;

        // Walk down powers of 2 until compaction succeeds or we reach _compactSize.
        // _compactSize is produced directly by PersistenceManager (batched persistable compactions).
        while (compactSize > _compactSize)
        {
            if (persistedSnapshotRepository.SnapshotCount < 2) return;

            long startingBlockNumber = ((blockNumber - 1) / compactSize) * compactSize;
            if (CompactRange(snapshotTo, startingBlockNumber, compactSize, isPersistable: false))
                return;

            compactSize /= 2;
        }
    }


    private readonly Histogram _persistedSnapshotSize =
        Prometheus.Metrics.CreateHistogram("persisted_snapshot_compacted_size", "persisted_snapshot_compacted_size", "size");
    private readonly Histogram _persistedSnapshotCompactTime =
        Prometheus.Metrics.CreateHistogram("persisted_snapshot_compact_time", "persisted_snapshot_compact_time", "size");

    private bool CompactRange(StateId snapshotTo, long startingBlockNumber, int compactSize, bool isPersistable)
    {
        using PersistedSnapshotList snapshots = persistedSnapshotRepository.AssembleSnapshotsForCompaction(snapshotTo, startingBlockNumber);
        if (snapshots.Count < 2) return false;

        if (snapshots[0].From.BlockNumber != startingBlockNumber)
        {
            if (_logger.IsDebug) _logger.Debug($"Unable to compile persisted snapshots to compact. {snapshots[0].From.BlockNumber} -> {snapshots[^1].To.BlockNumber}. Starting block number should be {startingBlockNumber}");
            return false;
        }

        if (_logger.IsDebug) _logger.Debug($"Compacting {snapshots.Count} persisted snapshots at block {snapshotTo.BlockNumber}, compact size {compactSize}, persistable {isPersistable}");

        StateId from = snapshots[0].From;
        StateId to = snapshots[^1].To;

        // Union of blob arena ids the inputs already reference. The merged snapshot
        // does not write any new RLP bytes; it just inherits these.
        HashSet<int> referencedBlobArenaIds = [];
        for (int i = 0; i < snapshots.Count; i++)
        {
            foreach (int id in snapshots[i].ReferencedBlobArenaIds)
                referencedBlobArenaIds.Add(id);
        }

        SnapshotLocation location;
        ArenaReservation reservation;
        long estimatedSize = 0;
        long bloomCapacity = 0;
        PersistedSnapshotBloomFilterManager bloomManager = persistedSnapshotRepository.BloomManager;
        for (int i = 0; i < snapshots.Count; i++)
        {
            estimatedSize += snapshots[i].Size;
            using PersistedSnapshotBloom srcBloom = bloomManager.LeaseOrSentinel(snapshots[i].To);
            bloomCapacity += srcBloom.KeyBloomCount;
        }

        if (estimatedSize > _maxCompactedSourceBytes)
        {
            if (_logger.IsDebug) _logger.Debug(
                $"Skipping compactSize={compactSize}: source bytes {estimatedSize} > {_maxCompactedSourceBytes} cap");
            return false;
        }

        BloomFilter? mergedBloom = _bloomBitsPerKey > 0 && bloomCapacity > 0
            ? new BloomFilter(bloomCapacity, _bloomBitsPerKey)
            : null;
        using (ArenaWriter arenaWriter = arenaManager.CreateWriter(estimatedSize, ArenaReservationTags.BlobBackedLarge))
        {
            long sw = Stopwatch.GetTimestamp();
            PersistedSnapshotBuilder.NWayMergeSnapshots<ArenaBufferWriter, ArenaBufferReader, NoOpPin>(
                snapshots, ref arenaWriter.GetWriter(), referencedBlobArenaIds, mergedBloom);

            for (int i = 0; i < snapshots.Count; i++)
            {
                PersistedSnapshot s = snapshots[i];
                bool isPersistableSize = s.To.BlockNumber - s.From.BlockNumber == _compactSize;
                if (!isPersistableSize)
                    s.AdviseDontNeed();
            }

            long len = arenaWriter.GetWriter().Written;
            _persistedSnapshotSize.WithLabels($"size{compactSize}").Observe(len);
            _persistedSnapshotCompactTime.WithLabels($"size{compactSize}").Observe(Stopwatch.GetTimestamp() - sw);

            (location, reservation) = arenaWriter.Complete();
        }

        persistedSnapshotRepository.AddCompactedSnapshot(from, to, location, reservation, referencedBlobArenaIds, isPersistable, mergedBloom);

        // The freshly-written compacted bytes are warm in the kernel page cache from the write
        // path; drop them so they don't crowd out the random-access read working set. Subsequent
        // reads will fault them back in on demand.
        reservation.AdviseDontNeed();

        // Bring the address-index BTree (outer column 0x01) back through the standard reader
        // so the PageResidencyTracker registers each index page. Bypassing via
        // RandomAccess.Read would warm the kernel cache but leave the tracker blind, letting
        // the next legitimate reader access collision-evict pages it never saw. The walk
        // touches index nodes only — per-address inner HSSTs stay cold.
        using (reservation.BeginWholeReadSession())
        {
            ArenaByteReader reader = reservation.CreateReader();
            PersistedSnapshotReader.WarmAddressIndex<ArenaByteReader, NoOpPin>(in reader);
        }

        Metrics.PersistedSnapshotCompactions++;
        Metrics.PersistedSnapshotCount = persistedSnapshotRepository.SnapshotCount;
        Metrics.PersistedSnapshotMemory = persistedSnapshotRepository.BaseSnapshotMemory;
        Metrics.CompactedPersistedSnapshotMemory = persistedSnapshotRepository.CompactedSnapshotMemory;
        Metrics.ArenaFileCount = persistedSnapshotRepository.ArenaFileCount;
        Metrics.ArenaMappedBytes = persistedSnapshotRepository.ArenaMappedBytes;
        return true;
    }
}
