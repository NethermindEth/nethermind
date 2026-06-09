// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Hsst;
using Nethermind.Core.Attributes;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Logarithmic compaction for the persisted snapshots, parameterised with a
/// <c>[minCompactSize, maxCompactSize]</c> band. A single instance is wired over the
/// repository. <see cref="DoCompactSnapshot"/> compacts a block's natural power-of-2 window —
/// the sub-<c>CompactSize</c> intermediates and the <c>&gt;CompactSize</c> hierarchical
/// merges; <see cref="DoCompactPersistable"/> produces the <c>CompactSize</c>-wide
/// persistable snapshot. Each window merges every persisted snapshot assembled within it into
/// one compacted snapshot when at least two are available — the window need not be fully
/// populated.
/// </summary>
public class PersistedSnapshotCompactor(
    IPersistedSnapshotRepository persistedSnapshotRepository,
    IArenaManager arenaManager,
    IFlatDbConfig config,
    ICompactionSchedule schedule,
    ILogManager logManager,
    PersistedSnapshotBloomFilterManager bloomManager,
    int minCompactSize,
    int maxCompactSize) : IPersistedSnapshotCompactor
{
    private readonly ILogger _logger = logManager.GetClassLogger<PersistedSnapshotCompactor>();
    private readonly ICompactionSchedule _schedule = schedule;
    private readonly int _minCompactSize = Math.Max(minCompactSize, 2);
    private readonly int _maxCompactSize = maxCompactSize;
    private readonly int _compactSize = config.CompactSize;
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
    private readonly long _maxCompactedSourceBytes = config.PersistedSnapshotMaxCompactedSourceBytes;

    /// <inheritdoc/>
    /// <remarks>
    /// Does nothing when the block's window is below <c>minCompactSize</c>, or exactly
    /// <c>CompactSize</c> — that window is the persistable's, produced by
    /// <see cref="DoCompactPersistable"/>.
    /// </remarks>
    public void DoCompactSnapshot(StateId snapshotTo)
    {
        if (_maxCompactSize < _minCompactSize) return;

        long blockNumber = snapshotTo.BlockNumber;
        if (blockNumber == 0) return;

        int alignment = (int)Math.Min(_schedule.GetHierarchicalCompactSize(blockNumber), _maxCompactSize);
        if (alignment < _minCompactSize) return;
        // The CompactSize-wide window is the persistable's — see DoCompactPersistable.
        if (alignment == _compactSize) return;

        if (persistedSnapshotRepository.SnapshotCount < 2) return;

        // The schedule alignment lives in offset-shifted space, but startingBlockNumber must
        // be the raw block number at the left edge of the window the alignment trigger
        // selects: (snapshotTo - alignment, snapshotTo]. Using ((b-1)/alignment)*alignment
        // here only works when offset == 0; with a non-zero offset it produces a shorter,
        // non-power-of-2 output span equal to (b mod alignment).
        long startingBlockNumber = blockNumber - alignment;
        CompactRange(snapshotTo, startingBlockNumber, alignment, isPersistable: false);
    }

    /// <inheritdoc/>
    public void DoCompactPersistable(StateId snapshotTo)
    {
        long blockNumber = snapshotTo.BlockNumber;
        if (!_schedule.IsFullCompactionBoundary(blockNumber)) return;

        if (persistedSnapshotRepository.SnapshotCount < 2) return;

        // The window is exactly (blockNumber - CompactSize, blockNumber].
        CompactRange(snapshotTo, blockNumber - _compactSize, _compactSize, isPersistable: true);
    }

    // Compact sizes are powers of 2; cache one StringLabel per sizeLabel so the
    // observe path skips the per-call string interpolation. Indexed by
    // BitOperations.Log2(compactSize). Filled lazily on first use.
    private StringLabel[]? _sizeLabelsByLog2;

    private StringLabel GetSizeLabel(int compactSize)
    {
        int log2 = BitOperations.Log2((uint)compactSize);
        StringLabel[] table = _sizeLabelsByLog2 ??= new StringLabel[32];
        return table[log2] ??= new StringLabel($"size{compactSize}");
    }

    private bool CompactRange(StateId snapshotTo, long startingBlockNumber, int compactSize, bool isPersistable)
    {
        using PersistedSnapshotList snapshots = persistedSnapshotRepository.AssembleSnapshotsForCompaction(snapshotTo, startingBlockNumber);
        if (snapshots.Count < 2) return false;

        if (_logger.IsDebug) _logger.Debug($"Compacting {snapshots.Count} persisted snapshots at block {snapshotTo.BlockNumber}, compact size {compactSize}, persistable {isPersistable}");

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
        using NativeMemoryListRef<WholeReadSessionView> viewsList = new(n, n);
        WholeReadSession[] sessionArr = sessionsList.UnsafeGetInternalArray();
        Span<WholeReadSessionView> views = viewsList.AsSpan();
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
                views[i] = sessionArr[i].GetView();

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
                    views, ref arenaWriter.GetWriter(), mergedBloom, PersistedSnapshotBuilder.SlotOptions(config));

                long len = arenaWriter.GetWriter().Written;
                StringLabel sizeLabel = GetSizeLabel(compactSize);
                Metrics.PersistedSnapshotCompactedSize.Observe(len, sizeLabel);
                Metrics.PersistedSnapshotCompactTime.Observe(Stopwatch.GetTimestamp() - sw, sizeLabel);

                (location, reservation) = arenaWriter.Complete();
            }

            // Durability barrier — fsync the metadata arena before the catalog records the
            // compacted entry. No blob fsync here: compaction does not write new blobs, it
            // only emits NodeRefs into existing base blob arenas (those were fsynced when
            // their respective base snapshots were converted).
            reservation.Fsync();

            // PersistedSnapshot's ctor (called from inside AddCompactedSnapshot) reads
            // the merged ref_ids back from its own metadata and leases each blob arena
            // file via a ref-struct iterator — no ushort[] materialisation here. The
            // returned snapshot is pre-leased; dispose it via `using` once we're done
            // with the post-write step.
            using (PersistedSnapshot compacted = persistedSnapshotRepository.AddCompactedSnapshot(from, to, location, reservation, mergedBloom, isPersistable))
            {
                if (compactSize < _compactSize)
                {
                    // Sub-CompactSize intermediate. Drop its freshly-written pages from the
                    // cache + tracker; they would otherwise sit hot until the snapshot is
                    // pruned.
                    compacted.Demote();
                }
                else
                {
                    // The persistable (== CompactSize) is scanned in full by
                    // PersistPersistedSnapshot; wider hierarchical merges are queried as
                    // snapshot-bundle skip pointers. Pre-fault the address column index so
                    // the first query doesn't chain inline page faults.
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
