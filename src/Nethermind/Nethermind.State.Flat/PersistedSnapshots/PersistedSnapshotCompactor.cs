// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
    PersistedSnapshotTier tier) : IPersistedSnapshotCompactor
{
    private readonly ILogger _logger = logManager.GetClassLogger<PersistedSnapshotCompactor>();
    private readonly int _minCompactSize = Math.Max(minCompactSize, 2);
    private readonly int _maxCompactSize = maxCompactSize;
    private readonly int _compactSize = config.CompactSize;
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
    private readonly long _maxCompactedSourceBytes = config.PersistedSnapshotMaxCompactedSourceBytes;
    private readonly PersistedSnapshotTier _tier = tier;

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

    // Compact sizes are powers of 2; cache one Histogram.Child per (tier, sizeLabel) so the
    // observe path is a single array read instead of two WithLabels lookups + a string
    // interpolation. Indexed by BitOperations.Log2(compactSize). Filled lazily on first use.
    private (Histogram.Child Size, Histogram.Child Time)[]? _sizeMetricsByLog2;

    private (Histogram.Child Size, Histogram.Child Time) GetSizeMetrics(int compactSize)
    {
        int log2 = BitOperations.Log2((uint)compactSize);
        (Histogram.Child Size, Histogram.Child Time)[] table =
            _sizeMetricsByLog2 ??= new (Histogram.Child, Histogram.Child)[32];
        (Histogram.Child Size, Histogram.Child Time) entry = table[log2];
        if (entry.Size is null)
        {
            string sizeLabel = $"size{compactSize}";
            entry = (
                _persistedSnapshotSize.WithLabels(_tier.Name, sizeLabel),
                _persistedSnapshotCompactTime.WithLabels(_tier.Name, sizeLabel));
            table[log2] = entry;
        }
        return entry;
    }

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

        // Open one WholeReadSession per source for the whole compaction. Every column
        // helper inside NWayMergeSnapshotsWithViews reads through these views — one mmap +
        // MADV_NORMAL on open and one MADV_DONTNEED on close per source, regardless of
        // how many columns we walk. ForgetTracker after the merge cleans the page-tracker
        // side; AdviseDontNeed on session dispose handles the page cache. The ref_ids
        // union is computed inside the merger directly from each source's metadata
        // value span — no pre-pass on this side.
        int n = snapshots.Count;
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        using NativeMemoryListRef<(IntPtr Ptr, long Len)> viewsList = new(n, n);
        WholeReadSession[] sessionArr = sessionsList.UnsafeGetInternalArray();
        Span<(IntPtr Ptr, long Len)> views = viewsList.AsSpan();
        try
        {
            long estimatedSize = 0;
            long bloomCapacity = 0;
            for (int i = 0; i < n; i++)
            {
                // Session dispose madvises the source's mmap range cold — the compacted
                // snapshot that supersedes these sources warms its own cache lazily on the
                // first read of each address, so there's no value in keeping these pages.
                sessionArr[i] = snapshots[i].BeginWholeReadSession();
                views[i] = sessionArr[i].GetRawView();

                estimatedSize += snapshots[i].Size;
                using PersistedSnapshotBloom srcBloom = bloomManager.LeaseOrSentinel(snapshots[i].To);
                bloomCapacity += srcBloom.BloomCount;
            }

            if (estimatedSize > _maxCompactedSourceBytes)
            {
                if (_logger.IsDebug) _logger.Debug(
                    $"Skipping compactSize={compactSize}: source bytes {estimatedSize} > {_maxCompactedSourceBytes} cap");
                return false;
            }

            // Bloom-disabled or empty-capacity case uses an AlwaysTrue sentinel so the
            // downstream AddCompactedSnapshot receives a non-null bloom uniformly.
            BloomFilter mergedBloom = _bloomBitsPerKey > 0 && bloomCapacity > 0
                ? new BloomFilter(bloomCapacity, _bloomBitsPerKey)
                : BloomFilter.AlwaysTrue();
            SnapshotLocation location;
            ArenaReservation reservation;
            using (ArenaWriter arenaWriter = arenaManager.CreateWriter(estimatedSize))
            {
                long sw = Stopwatch.GetTimestamp();
                PersistedSnapshotMerger.NWayMergeSnapshotsWithViews<ArenaBufferWriter, ArenaBufferReader, NoOpPin>(
                    views, ref arenaWriter.GetWriter(), mergedBloom);

                long len = arenaWriter.GetWriter().Written;
                (Histogram.Child sizeChild, Histogram.Child timeChild) = GetSizeMetrics(compactSize);
                sizeChild.Observe(len);
                timeChild.Observe(Stopwatch.GetTimestamp() - sw);

                (location, reservation) = arenaWriter.Complete();
            }

            // PersistedSnapshot's ctor (called from inside AddCompactedSnapshot) reads
            // the merged ref_ids back from its own metadata and leases each blob arena
            // file via a ref-struct iterator — no ushort[] materialisation here. The
            // returned snapshot is pre-leased; dispose it via `using` once we're done
            // with the post-write step.
            using (PersistedSnapshot compacted = persistedSnapshotRepository.AddCompactedSnapshot(from, to, location, reservation, mergedBloom))
            {
                if (_tier == PersistedSnapshotTier.Small && compactSize == _maxCompactSize)
                {
                    // Invariant: small tier's _maxCompactSize is CompactSize/2, so this
                    // branch fires only on the topmost small-tier output. No further
                    // small-tier compaction will absorb it (the large tier writes its
                    // base snapshot from scratch via PersistenceManager, not by
                    // re-reading small-tier outputs), so its pages would otherwise sit
                    // hot in the page cache and tracker until the snapshot is finally
                    // pruned.
                    compacted.Demote();
                }
                else if (_tier == PersistedSnapshotTier.Large)
                {
                    WarmAddressColumnIndex(compacted);
                }
            }

            Metrics.PersistedSnapshotCompactions++;
            // PersistedSnapshotCount / PersistedSnapshotMemory / CompactedPersistedSnapshotMemory
            // are now mutated delta-wise inside the repo at every add/remove site
            // (AddCompactedSnapshot just ran above; the per-source disposals happen on Dispose).
            // Arena file/byte counters update themselves via push deltas in ArenaManager.
            return true;
        }
        finally
        {
            for (int i = 0; i < n; i++) sessionArr[i]?.Dispose();
        }
    }

    /// <summary>
    /// Pre-fault the address column's index region of a freshly-written large-tier
    /// snapshot so its BTree separators / page directory land in the page-residency
    /// tracker. Without this, the first query walking the address column takes a chain
    /// of inline minor page faults.
    /// </summary>
    /// <remarks>
    /// The index region is the byte range from the end of the last data entry to the end
    /// of the address column's HSST bound (not the arena/file EOF). Locating it requires
    /// (a) the column bound and (b) the bound of the largest data entry. The largest entry
    /// is found via <c>TrySeekFloor</c> with a 20-byte all-<c>0xFF</c> key — addresses are
    /// 20 bytes, so this floor-seek always lands on the rightmost entry of the BTree.
    /// </remarks>
    internal static void WarmAddressColumnIndex(PersistedSnapshot snapshot)
    {
        ArenaReservation reservation = snapshot.Reservation;
        ArenaByteReader reader = reservation.CreateReader();

        if (!PersistedSnapshotReader.TryGetAddressColumnBound<ArenaByteReader, NoOpPin>(
                in reader, out Bound columnBound))
            return;

        using HsstReader<ArenaByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(PersistedSnapshotTags.AccountColumnTag, out _))
            return;
        Span<byte> maxKey = stackalloc byte[Address.Size];
        maxKey.Fill(0xFF);
        if (!r.TrySeekFloor(maxKey, out Bound lastEntry))
            return;

        long dataEnd = lastEntry.Offset + lastEntry.Length;
        long columnEnd = columnBound.Offset + columnBound.Length;
        long indexLen = columnEnd - dataEnd;
        if (indexLen <= 0) return;

        long indexStartLocal = dataEnd - reservation.Offset;
        reservation.TouchRangePopulate(indexStartLocal, indexLen);
    }
}
