// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;
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
/// Logarithmic compaction for the persisted snapshots, bounded above by the
/// <c>PersistedSnapshotMaxCompactSize</c> ceiling. A single instance is wired over the
/// repository. <see cref="DoCompactSnapshot"/> compacts a block's natural power-of-2 window —
/// the sub-<c>CompactSize</c> intermediates and the <c>&gt;CompactSize</c> hierarchical
/// merges; <see cref="DoCompactPersistable"/> produces the <c>CompactSize</c>-wide
/// persistable snapshot. Each window merges every persisted snapshot assembled within it into
/// one compacted snapshot when at least two are available — the window need not be fully
/// populated.
/// </summary>
public class PersistedSnapshotCompactor(
    ISnapshotRepository snapshotRepository,
    IArenaManager arenaManager,
    IFlatDbConfig config,
    ICompactionSchedule schedule,
    ILogManager logManager) : IPersistedSnapshotCompactor
{
    private readonly ILogger _logger = logManager.GetClassLogger<PersistedSnapshotCompactor>();
    private readonly ICompactionSchedule _schedule = schedule;
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
    private readonly long _maxCompactedSourceBytes = config.PersistedSnapshotMaxCompactedSourceBytes;

    private readonly Channel<ArrayPoolList<StateId>> _compactPersistedJobs = Channel.CreateBounded<ArrayPoolList<StateId>>(16);
    private readonly Channel<StateId> _boundaryCompactJobs = Channel.CreateBounded<StateId>(16);
    private readonly CancellationTokenSource _cancelTokenSource = new();
    private Task? _compactPersistedTask;
    private Task[]? _boundaryCompactorTasks;
    private int _disposed;

    private const int BoundaryCompactorWorkerCount = 4;

    /// <inheritdoc/>
    public void Enqueue(ArrayPoolList<StateId> batch)
    {
        EnsureStarted();
        _compactPersistedJobs.Writer.WriteAsync(batch).AsTask().Wait();
    }

    private Task EnsureStarted()
    {
        _compactPersistedTask ??= RunPersistedCompactor(_cancelTokenSource.Token);
        if (_boundaryCompactorTasks is null)
        {
            Task[] tasks = new Task[BoundaryCompactorWorkerCount];
            for (int i = 0; i < BoundaryCompactorWorkerCount; i++)
                tasks[i] = RunBoundaryCompactor(_cancelTokenSource.Token);
            _boundaryCompactorTasks = tasks;
        }
        return _compactPersistedTask;
    }

    private async Task RunPersistedCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ArrayPoolList<StateId> batch in _compactPersistedJobs.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await ProcessCompactBatch(batch);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error compacting persisted snapshot batch. {ex}");
                }
                finally
                {
                    batch.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            while (_compactPersistedJobs.Reader.TryRead(out ArrayPoolList<StateId>? batch))
                batch.Dispose();
        }
    }

    private async Task ProcessCompactBatch(ArrayPoolList<StateId> batch)
    {
        if (batch.Count == 0) return;

        using ArrayPoolList<StateId> boundaries = new(batch.Count);
        SortedDictionary<int, List<StateId>> buckets = [];
        for (int i = 0; i < batch.Count; i++)
        {
            StateId s = batch[i];
            long b = s.BlockNumber;
            if (b == 0) continue;

            if (_schedule.IsFullCompactionBoundary(b))
            {
                // A CompactSize boundary — its persistable is produced below via
                // DoCompactPersistable, so it is not bucketed for DoCompactSnapshot.
                boundaries.Add(s);
                continue;
            }

            // Non-boundary: bucket by power-of-2 alignment (always < CompactSize).
            int compactSize = (int)_schedule.GetHierarchicalCompactSize(b);
            if (!buckets.TryGetValue(compactSize, out List<StateId>? bucket))
                buckets[compactSize] = bucket = [];
            bucket.Add(s);
        }

        // Ascending bucket order: each sub-CompactSize layer's inputs (the previous layer's
        // outputs) exist before it runs.
        foreach (KeyValuePair<int, List<StateId>> kv in buckets)
            Parallel.ForEach(kv.Value, state => DoCompactSnapshot(state));

        // The sub-CompactSize layers are in place — produce each boundary's persistable.
        foreach (StateId boundary in boundaries)
            DoCompactPersistable(boundary);

        // Hand every boundary to the boundary compactor. DoCompactSnapshot there no-ops for a
        // boundary whose highest power of two is exactly CompactSize (no >CompactSize merge window),
        // so there's no need to pre-filter here.
        foreach (StateId boundary in boundaries)
            await _boundaryCompactJobs.Writer.WriteAsync(boundary, _cancelTokenSource.Token);
    }

    private async Task RunBoundaryCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (StateId state in _boundaryCompactJobs.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    // The persistable for this boundary was already produced in
                    // ProcessCompactBatch; DoCompactSnapshot here only does the
                    // >CompactSize hierarchical merges.
                    DoCompactSnapshot(state);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error compacting boundary persisted snapshot {state}. {ex}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancelTokenSource.Cancel();
        _compactPersistedJobs.Writer.Complete();
        _boundaryCompactJobs.Writer.Complete();
        if (_compactPersistedTask is not null)
            await _compactPersistedTask;
        if (_boundaryCompactorTasks is not null)
            await Task.WhenAll(_boundaryCompactorTasks);
        _cancelTokenSource.Dispose();
    }

    /// <summary>
    /// Compact the persisted snapshots ending at <paramref name="snapshotTo"/> over the block's
    /// natural power-of-2 window. Produces sub-<c>CompactSize</c> intermediates and the
    /// <c>&gt;CompactSize</c> hierarchical merges; the <c>CompactSize</c>-wide window is
    /// reserved for <see cref="DoCompactPersistable"/>. Invoked by the background batch worker
    /// (see <see cref="Enqueue"/>); not part of <see cref="IPersistedSnapshotCompactor"/>.
    /// </summary>
    /// <remarks>
    /// Does nothing when the block's window is a single snapshot (nothing to merge), or exactly
    /// <c>CompactSize</c> — that window is the persistable's, produced by
    /// <see cref="DoCompactPersistable"/>.
    /// </remarks>
    public void DoCompactSnapshot(StateId snapshotTo)
    {
        if (_schedule.GetHierarchicalCompactionWindow(snapshotTo.BlockNumber) is not { } window) return;
        if (snapshotRepository.PersistedSnapshotCount < 2) return;

        CompactRange(snapshotTo, window.StartBlock, window.Size, isPersistable: false);
    }

    /// <summary>
    /// Produce the <c>CompactSize</c>-wide persistable snapshot ending at the boundary
    /// block <paramref name="snapshotTo"/> — the snapshot <c>PersistenceManager</c> writes to
    /// RocksDB. Invoked by the background batch worker (see <see cref="Enqueue"/>); not part of
    /// <see cref="IPersistedSnapshotCompactor"/>.
    /// </summary>
    public void DoCompactPersistable(StateId snapshotTo)
    {
        long blockNumber = snapshotTo.BlockNumber;
        if (!_schedule.IsFullCompactionBoundary(blockNumber)) return;

        if (snapshotRepository.PersistedSnapshotCount < 2) return;

        CompactionWindow window = _schedule.GetPersistableCompactionWindow(blockNumber);
        CompactRange(snapshotTo, window.StartBlock, window.Size, isPersistable: true);
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
        using PersistedSnapshotList snapshots = snapshotRepository.AssembleSnapshotsForCompaction(snapshotTo, startingBlockNumber);
        if (snapshots.Count < 2) return false;

        if (_logger.IsDebug) _logger.Debug($"Compacting {snapshots.Count} persisted snapshots at block {snapshotTo.BlockNumber}, compact size {compactSize}, persistable {isPersistable}");

        StateId from = snapshots[0].From;
        StateId to = snapshots[^1].To;

        // Open one WholeReadSession per source for the whole compaction. Every column
        // helper inside NWayMergeSnapshots reads through these views — one mmap +
        // MADV_NORMAL on open and one MADV_DONTNEED on close per source, regardless of
        // how many columns we walk. ForgetTracker after the merge cleans the page-tracker
        // side; AdviseDontNeed on session dispose handles the page cache. The ref_ids
        // union is computed inside the merger directly from each source's metadata
        // value span — no pre-pass on this side.
        int n = snapshots.Count;
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        WholeReadSession[] sessionArr = sessionsList.UnsafeGetInternalArray();
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

                estimatedSize += snapshots[i].Size;
                // Each source carries its own bloom; sum their key counts to size the merge.
                // The AlwaysTrue placeholder reports Count == 0, so a not-yet-built source just
                // contributes nothing — same as the old manager's sentinel did.
                bloomCapacity += snapshots[i].Bloom.Count;
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
                PersistedSnapshotMerger.NWayMergeSnapshots<ArenaBufferWriter, WholeReadSession, WholeReadSessionReader, NoOpPin>(
                    sessionsList.AsSpan(), ref arenaWriter.GetWriter(), mergedBloom);

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
            using (PersistedSnapshot compacted = snapshotRepository.AddCompactedSnapshot(from, to, location, reservation, mergedBloom, isPersistable))
            {
                if (_schedule.IsIntermediateWindow(compactSize))
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
