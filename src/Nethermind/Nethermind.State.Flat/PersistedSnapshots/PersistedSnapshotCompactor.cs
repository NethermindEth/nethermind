// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Db;
using Nethermind.Logging;
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

        // Collect all base snapshot IDs that the compacted result will reference via NodeRefs
        HashSet<int> referencedIds = [];
        for (int i = 0; i < snapshots.Count; i++)
        {
            if (snapshots[i].Type == PersistedSnapshotType.Full)
            {
                referencedIds.Add(snapshots[i].Id);
            }
            else if (snapshots[i].ReferencedSnapshotIds is int[] ids)
            {
                for (int j = 0; j < ids.Length; j++) referencedIds.Add(ids[j]);
            }
        }

        SnapshotLocation location;
        ArenaReservation reservation;
        long estimatedSize = 0;
        long bloomCapacity = 0;
        for (int i = 0; i < snapshots.Count; i++)
        {
            estimatedSize += snapshots[i].Size;
            bloomCapacity += snapshots[i].KeyBloomCount;
        }

        const long MaxCompactedSourceBytes = 2L * 1024 * 1024 * 1024;
        if (estimatedSize > MaxCompactedSourceBytes)
        {
            if (_logger.IsDebug) _logger.Debug(
                $"Skipping compactSize={compactSize}: source bytes {estimatedSize} > 2 GiB cap");
            return false;
        }

        BloomFilter? mergedBloom = _bloomBitsPerKey > 0 && bloomCapacity > 0
            ? new BloomFilter(bloomCapacity, _bloomBitsPerKey)
            : null;
        using (ArenaWriter arenaWriter = arenaManager.CreateWriter((int)estimatedSize, ArenaReservationTags.LinkedCompacted))
        {
            long sw = Stopwatch.GetTimestamp();
            PersistedSnapshotBuilder.NWayMergeSnapshots(snapshots, ref arenaWriter.GetWriter(), referencedIds, mergedBloom);

            for (int i = 0; i < snapshots.Count; i++)
            {
                PersistedSnapshot s = snapshots[i];
                bool isPersistableSize = s.To.BlockNumber - s.From.BlockNumber == _compactSize;
                if (s.Type != PersistedSnapshotType.Full || !isPersistableSize)
                    s.AdviseDontNeed();
            }

            int len = arenaWriter.GetWriter().Written;
            _persistedSnapshotSize.WithLabels($"size{compactSize}").Observe(len);
            _persistedSnapshotCompactTime.WithLabels($"size{compactSize}").Observe(Stopwatch.GetTimestamp() - sw);

            (location, reservation) = arenaWriter.Complete();

            if (_validatePersistedSnapshot)
            {
                PersistedSnapshot compacted = new(0, from, to, PersistedSnapshotType.Linked, reservation);
                try
                {
                    PersistedSnapshotUtils.ValidateCompactedPersistedSnapshot(compacted, snapshots, true);
                }
                finally
                {
                    compacted.Dispose();
                }
            }
        }

        persistedSnapshotRepository.AddCompactedSnapshot(from, to, location, reservation, referencedIds, isPersistable, mergedBloom);

        // The freshly-written compacted bytes are warm in the kernel page cache from the write
        // path; drop them so they don't crowd out the random-access read working set. Subsequent
        // reads will fault them back in on demand.
        reservation.AdviseDontNeed();

        Metrics.PersistedSnapshotCompactions++;
        Metrics.PersistedSnapshotCount = persistedSnapshotRepository.SnapshotCount;
        Metrics.PersistedSnapshotMemory = persistedSnapshotRepository.BaseSnapshotMemory;
        Metrics.CompactedPersistedSnapshotMemory = persistedSnapshotRepository.CompactedSnapshotMemory;
        Metrics.ArenaFileCount = persistedSnapshotRepository.ArenaFileCount;
        Metrics.ArenaMappedBytes = persistedSnapshotRepository.ArenaMappedBytes;
        return true;
    }
}
