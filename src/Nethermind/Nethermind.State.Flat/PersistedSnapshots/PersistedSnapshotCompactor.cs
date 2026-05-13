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
/// Logarithmic compaction for one tier's persisted snapshots. Each instance is
/// parameterised with a <c>[minCompactSize, maxCompactSize]</c> band; it walks
/// powers of 2 downward from the block's natural alignment (capped at
/// <c>maxCompactSize</c>) and attempts to merge into the largest size that
/// fits. The small-tier instance is wired with <c>max = CompactSize/2</c> so
/// it never produces a <c>CompactSize</c> result (that size is produced
/// directly by <c>PersistenceManager</c> into the large tier). The large-tier
/// instance is wired with <c>min = 2 * CompactSize</c>.
/// </summary>
public class PersistedSnapshotCompactor(
    IPersistedSnapshotRepository persistedSnapshotRepository,
    IArenaManager arenaManager,
    IFlatDbConfig config,
    ILogManager logManager,
    PersistedSnapshotBloomFilterManager bloomManager,
    int minCompactSize,
    int maxCompactSize,
    PersistedSnapshotTier tier,
    string reservationTag) : IPersistedSnapshotCompactor
{
    private readonly ILogger _logger = logManager.GetClassLogger<PersistedSnapshotCompactor>();
    private readonly int _minCompactSize = Math.Max(minCompactSize, 2);
    private readonly int _maxCompactSize = maxCompactSize;
    private readonly int _compactSize = config.CompactSize;
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
    private readonly long _maxCompactedSourceBytes = config.PersistedSnapshotMaxCompactedSourceBytes;
    private readonly PersistedSnapshotTier _tier = tier;
    private readonly string _reservationTag = reservationTag;

    /// <summary>
    /// Try to compact persisted snapshots using logarithmic compaction. Walks
    /// powers of 2 downward from the block's natural alignment (capped at
    /// <c>maxCompactSize</c>), attempting each one until a merge succeeds or
    /// the size drops below <c>minCompactSize</c>.
    /// </summary>
    public void DoCompactSnapshot(StateId snapshotTo)
    {
        if (_maxCompactSize < _minCompactSize) return;

        long blockNumber = snapshotTo.BlockNumber;
        if (blockNumber == 0) return;

        int alignment = (int)Math.Min(blockNumber & -blockNumber, _maxCompactSize);
        int compactSize = alignment;
        while (compactSize >= _minCompactSize)
        {
            if (persistedSnapshotRepository.SnapshotCount < 2) return;

            long startingBlockNumber = ((blockNumber - 1) / compactSize) * compactSize;
            if (CompactRange(snapshotTo, startingBlockNumber, compactSize))
                return;

            compactSize /= 2;
        }
    }

    // Histograms gain a `tier` label so the two instances' samples are distinguishable
    // in dashboards.
    private readonly Histogram _persistedSnapshotSize =
        Prometheus.Metrics.CreateHistogram("persisted_snapshot_compacted_size", "persisted_snapshot_compacted_size", "tier", "size");
    private readonly Histogram _persistedSnapshotCompactTime =
        Prometheus.Metrics.CreateHistogram("persisted_snapshot_compact_time", "persisted_snapshot_compact_time", "tier", "size");

    private bool CompactRange(StateId snapshotTo, long startingBlockNumber, int compactSize)
    {
        using PersistedSnapshotList snapshots = persistedSnapshotRepository.AssembleSnapshotsForCompaction(snapshotTo, startingBlockNumber);
        if (snapshots.Count < 2) return false;

        if (snapshots[0].From.BlockNumber != startingBlockNumber)
        {
            if (_logger.IsDebug) _logger.Debug($"Unable to compile persisted snapshots to compact. {snapshots[0].From.BlockNumber} -> {snapshots[^1].To.BlockNumber}. Starting block number should be {startingBlockNumber}");
            return false;
        }

        if (_logger.IsDebug) _logger.Debug($"Compacting {snapshots.Count} persisted snapshots at block {snapshotTo.BlockNumber}, compact size {compactSize}, tier {_tier}");

        StateId from = snapshots[0].From;
        StateId to = snapshots[^1].To;

        // Union of blob arena ids the inputs already reference. The merged snapshot
        // does not write any new RLP bytes; it just inherits these. Each input's id list
        // is materialised once from its on-disk metadata HSST (no in-memory cache).
        HashSet<ushort> referencedBlobArenaIds = [];
        for (int i = 0; i < snapshots.Count; i++)
        {
            ushort[]? ids = snapshots[i].ReadReferencedBlobArenaIds();
            if (ids is null) continue;
            foreach (ushort id in ids)
                referencedBlobArenaIds.Add(id);
        }

        SnapshotLocation location;
        ArenaReservation reservation;
        long estimatedSize = 0;
        long bloomCapacity = 0;
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
        using (ArenaWriter arenaWriter = arenaManager.CreateWriter(estimatedSize, _reservationTag))
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
            _persistedSnapshotSize.WithLabels(_tier.Name, $"size{compactSize}").Observe(len);
            _persistedSnapshotCompactTime.WithLabels(_tier.Name, $"size{compactSize}").Observe(Stopwatch.GetTimestamp() - sw);

            (location, reservation) = arenaWriter.Complete();
        }

        persistedSnapshotRepository.AddCompactedSnapshot(from, to, location, reservation, referencedBlobArenaIds, mergedBloom);

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
        // Arena file/byte counters update themselves via push deltas in ArenaManager —
        // no manual recompute needed here.
        return true;
    }
}
