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
/// Logarithmic compaction for one tier's persisted snapshots. Two instances are
/// wired: a <see cref="Mode.Small"/> compactor merges short-range snapshots
/// within the small tier (every merge stays strictly &lt; <c>CompactSize</c>),
/// and a <see cref="Mode.Large"/> compactor merges <c>CompactSize</c>-aligned
/// snapshots upward (2×, 4×, ... <c>CompactSize</c>, up to
/// <c>PersistedSnapshotMaxCompactSize</c>). The boundary at <c>CompactSize</c>
/// is exclusive on the small side (its compactor never produces a
/// <c>CompactSize</c> result — that comes from the in-memory compactor and is
/// fed into the large repo by <c>PersistenceManager</c>).
/// </summary>
public class PersistedSnapshotCompactor(
    IPersistedSnapshotRepository persistedSnapshotRepository,
    IArenaManager arenaManager,
    IFlatDbConfig config,
    ILogManager logManager,
    PersistedSnapshotCompactor.Mode mode) : IPersistedSnapshotCompactor
{
    public enum Mode
    {
        Small,
        Large,
    }

    private readonly ILogger _logger = logManager.GetClassLogger<PersistedSnapshotCompactor>();
    private readonly int _compactSize = config.CompactSize;
    private readonly int _persistedSnapshotMaxCompactSize = config.PersistedSnapshotMaxCompactSize;
    private readonly int _minCompactSize = Math.Max(config.MinCompactSize, 2);
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
    private readonly long _maxCompactedSourceBytes = config.PersistedSnapshotMaxCompactedSourceBytes;
    private readonly Mode _mode = mode;
    private readonly string _tierLabel = mode == Mode.Small ? "small" : "large";

    /// <summary>
    /// Try to compact persisted snapshots using logarithmic compaction. The
    /// power-of-2 walk direction and the size-band boundary depend on
    /// <see cref="_mode"/>:
    /// <list type="bullet">
    ///   <item><see cref="Mode.Large"/>: walk <c>compactSize</c> downward from the
    ///   block's natural alignment, attempting each power of 2 strictly greater
    ///   than <c>CompactSize</c>. Produces 2×, 4×, ... <c>CompactSize</c> merges.</item>
    ///   <item><see cref="Mode.Small"/>: walk upward from <c>MinCompactSize</c>,
    ///   attempting each power of 2 strictly less than <c>CompactSize</c>.
    ///   Produces 2×, 4×, ... merges that stay below the <c>CompactSize</c>
    ///   boundary — the small tier never produces a <c>CompactSize</c> result.</item>
    /// </list>
    /// </summary>
    public void DoCompactSnapshot(StateId snapshotTo)
    {
        if (_compactSize <= 0) return;

        long blockNumber = snapshotTo.BlockNumber;
        if (blockNumber == 0) return;

        int alignment = (int)Math.Min(blockNumber & -blockNumber, _persistedSnapshotMaxCompactSize);
        if (alignment < _minCompactSize) return;

        if (_mode == Mode.Large)
        {
            int compactSize = alignment;
            // Walk down powers of 2 until compaction succeeds or we reach _compactSize.
            // _compactSize is produced directly by PersistenceManager (batched persistable
            // compactions) into the large repo as a base — never re-produced here.
            while (compactSize > _compactSize)
            {
                if (persistedSnapshotRepository.SnapshotCount < 2) return;

                long startingBlockNumber = ((blockNumber - 1) / compactSize) * compactSize;
                if (CompactRange(snapshotTo, startingBlockNumber, compactSize))
                    return;

                compactSize /= 2;
            }
        }
        else // Mode.Small
        {
            // Largest power of 2 strictly less than _compactSize that the block is
            // aligned to. If alignment >= _compactSize we'd produce a CompactSize
            // (or larger) result — out of band for the small tier.
            int compactSize = Math.Min(alignment, _compactSize / 2);
            while (compactSize >= _minCompactSize)
            {
                if (persistedSnapshotRepository.SnapshotCount < 2) return;

                long startingBlockNumber = ((blockNumber - 1) / compactSize) * compactSize;
                if (CompactRange(snapshotTo, startingBlockNumber, compactSize))
                    return;

                compactSize /= 2;
            }
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

        if (_logger.IsDebug) _logger.Debug($"Compacting {snapshots.Count} persisted snapshots at block {snapshotTo.BlockNumber}, compact size {compactSize}, tier {_tierLabel}");

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
        string reservationTag = _mode == Mode.Small ? ArenaReservationTags.BlobBackedSmall : ArenaReservationTags.BlobBackedLarge;
        using (ArenaWriter arenaWriter = arenaManager.CreateWriter(estimatedSize, reservationTag))
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
            _persistedSnapshotSize.WithLabels(_tierLabel, $"size{compactSize}").Observe(len);
            _persistedSnapshotCompactTime.WithLabels(_tierLabel, $"size{compactSize}").Observe(Stopwatch.GetTimestamp() - sw);

            (location, reservation) = arenaWriter.Complete();
        }

        persistedSnapshotRepository.AddCompactedSnapshot(from, to, location, reservation, referencedBlobArenaIds, isPersistable: false, mergedBloom);

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
