// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Logarithmic compaction for the persisted snapshots, bounded above by the
/// <c>PersistedSnapshotMaxCompactSize</c> ceiling. A single instance is wired over the
/// repository. <see cref="DoCompactSnapshot"/> compacts a block's natural power-of-2 window —
/// the sub-<c>CompactSize</c> intermediates and the <c>&gt;CompactSize</c> merges;
/// <see cref="DoCompactCompactSized"/> produces the <c>CompactSize</c>-wide
/// CompactSized snapshot. Each window merges every persisted snapshot assembled within it into
/// one compacted snapshot when at least two are available — the window need not be fully
/// populated.
/// </summary>
/// <remarks>
/// Takes a dependency on <see cref="IPersistedSnapshotLoader"/> purely to order shutdown: the
/// edge makes DI activate the loader first and so dispose this compactor before it, draining the
/// bucket-touching worker tasks (via <see cref="DisposeAsync"/>) before the loader's
/// <c>Dispose</c> runs <see cref="ISnapshotRepository.MarkPersistedTierForShutdown"/>. Without it
/// a worker could index a new persisted snapshot after the tier is marked, losing its files.
/// </remarks>
public class PersistedSnapshotCompactor(
    ISnapshotRepository snapshotRepository,
    IArenaManager arenaManager,
    BlobArenaManager blobs,
    ISnapshotCatalog catalog,
    IFlatDbConfig config,
    ICompactionSchedule schedule,
    IPersistedSnapshotLoader loader,
    IProcessExitSource processExitSource,
    ILogManager logManager) : IPersistedSnapshotCompactor
{
    // Held only to anchor the disposal order documented above (loader disposed after this).
    private readonly IPersistedSnapshotLoader _disposeOrderingAnchor = loader;
    private readonly ILogger _logger = logManager.GetClassLogger<PersistedSnapshotCompactor>();
    private readonly ISnapshotCatalog _catalog = catalog;
    private readonly ICompactionSchedule _schedule = schedule;
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;

    private readonly Channel<(ArrayPoolList<StateId> Batch, long PersistedBlockNumber)> _compactPersistedJobs = Channel.CreateBounded<(ArrayPoolList<StateId>, long)>(16);
    private readonly Channel<(StateId Boundary, long PersistedBlockNumber)> _boundaryCompactJobs = Channel.CreateBounded<(StateId, long)>(16);
    // Background workers and their in-flight compaction observe process-exit directly; graceful
    // disposal instead completes the channels and drains the remaining work (see DisposeAsync).
    private readonly CancellationToken _shutdownToken = processExitSource.Token;
    private Task? _compactPersistedTask;
    private Task[]? _boundaryCompactorTasks;
    private int _disposed;

    private const int BoundaryCompactorWorkerCount = 4;

    /// <inheritdoc/>
    public async ValueTask EnqueueAsync(ArrayPoolList<StateId> batch, long persistedBlockNumber, CancellationToken cancellationToken)
    {
        // Fire-and-forget: EnsureStarted returns the long-running compactor task, which must not be awaited.
        _ = EnsureStarted();
        try
        {
            // Awaits a free slot on the bounded queue, providing backpressure without blocking a thread;
            // the caller's token releases the wait on shutdown.
            await _compactPersistedJobs.Writer.WriteAsync((batch, persistedBlockNumber), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The batch never entered the channel, so dispose the handoff we still own.
            batch.Dispose();
            throw;
        }
    }

    private Task EnsureStarted()
    {
        _compactPersistedTask ??= RunPersistedCompactor(_shutdownToken);
        if (_boundaryCompactorTasks is null)
        {
            Task[] tasks = new Task[BoundaryCompactorWorkerCount];
            for (int i = 0; i < BoundaryCompactorWorkerCount; i++)
                tasks[i] = RunBoundaryCompactor(_shutdownToken);
            _boundaryCompactorTasks = tasks;
        }
        return _compactPersistedTask;
    }

    private async Task RunPersistedCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach ((ArrayPoolList<StateId> batch, long persistedBlockNumber) in _compactPersistedJobs.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await ProcessCompactBatch(batch, persistedBlockNumber, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
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
            while (_compactPersistedJobs.Reader.TryRead(out (ArrayPoolList<StateId> Batch, long PersistedBlockNumber) item))
                item.Batch.Dispose();
        }
    }

    private async Task ProcessCompactBatch(ArrayPoolList<StateId> batch, long persistedBlockNumber, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;

        using ArrayPoolList<StateId> largeBoundaries = new(batch.Count);
        using ArrayPoolList<StateId> compactSizeBoundaries = new(batch.Count);
        SortedDictionary<int, List<StateId>> buckets = [];
        for (int i = 0; i < batch.Count; i++)
        {
            StateId s = batch[i];
            long b = s.BlockNumber;
            if (b == 0) continue;

            if (_schedule.IsLargeCompactionBoundary(b))
            {
                // Large boundary: needs the CompactSized snapshot AND the >CompactSize merge.
                largeBoundaries.Add(s);
                compactSizeBoundaries.Add(s);
            }
            else if (_schedule.IsCompactSizeBoundary(b))
            {
                // Plain CompactSize boundary: only the CompactSized.
                compactSizeBoundaries.Add(s);
            }
            else
            {
                // Non-boundary: bucket by power-of-2 alignment (always < CompactSize).
                int compactSize = (int)_schedule.GetPersistedSnapshotCompactSize(b);
                if (!buckets.TryGetValue(compactSize, out List<StateId>? bucket))
                    buckets[compactSize] = bucket = [];
                bucket.Add(s);
            }
        }

        // Ascending bucket order: each sub-CompactSize layer's inputs (the previous layer's
        // outputs) exist before it runs.
        foreach (KeyValuePair<int, List<StateId>> kv in buckets)
            Parallel.ForEach(kv.Value, new ParallelOptions { CancellationToken = cancellationToken }, state => DoCompactSnapshot(state, persistedBlockNumber));

        // Every boundary — CompactSize and large alike — lands on a CompactSize multiple, so each
        // needs its CompactSized snapshot for RocksDB (persistence advances one CompactSize
        // per step); both kinds are collected in compactSizeBoundaries above.
        foreach (StateId boundary in compactSizeBoundaries)
            DoCompactCompactSized(boundary);

        // Large boundaries additionally carry a >CompactSize merge. These can be a few GB large, so
        // they are handed to the boundary compactor to run as a separate background task rather than
        // blocking this batch worker.
        foreach (StateId boundary in largeBoundaries)
            await _boundaryCompactJobs.Writer.WriteAsync((boundary, persistedBlockNumber), cancellationToken);
    }

    private async Task RunBoundaryCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach ((StateId state, long persistedBlockNumber) in _boundaryCompactJobs.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    // Only large boundaries reach this channel; their CompactSized was already
                    // produced in ProcessCompactBatch, so DoCompactSnapshot here does the
                    // >CompactSize merge.
                    DoCompactSnapshot(state, persistedBlockNumber);
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
        // Complete and drain the persisted stage first so any boundary jobs it produces are written
        // before the boundary channel is completed; on process exit the shared token has already
        // cancelled both stages, so these awaits return promptly instead of draining.
        _compactPersistedJobs.Writer.Complete();
        if (_compactPersistedTask is not null)
            await _compactPersistedTask;
        _boundaryCompactJobs.Writer.Complete();
        if (_boundaryCompactorTasks is not null)
            await Task.WhenAll(_boundaryCompactorTasks);
    }

    /// <summary>
    /// Compact the persisted snapshots ending at <paramref name="snapshotTo"/> over the block's
    /// natural power-of-2 window. Produces sub-<c>CompactSize</c> intermediates and the
    /// <c>&gt;CompactSize</c> merges; the <c>CompactSize</c>-wide window is
    /// reserved for <see cref="DoCompactCompactSized"/>. Invoked by the background batch worker
    /// (see <see cref="Enqueue"/>); not part of <see cref="IPersistedSnapshotCompactor"/>.
    /// </summary>
    /// <remarks>
    /// Does nothing when the block's window is a single snapshot (nothing to merge). The
    /// <c>CompactSize</c>-wide window is produced by <see cref="DoCompactCompactSized"/>;
    /// <see cref="ProcessCompactBatch"/> routes those boundaries away from here, so this method
    /// only ever sees sub-<c>CompactSize</c> intermediates and <c>&gt;CompactSize</c> merges.
    /// </remarks>
    public void DoCompactSnapshot(StateId snapshotTo, long persistedBlockNumber = 0)
    {
        long blockNumber = snapshotTo.BlockNumber;
        int size = (int)_schedule.GetPersistedSnapshotCompactSize(blockNumber);
        // size 1 is a single snapshot — nothing to merge.
        if (size <= 1) return;
        if (snapshotRepository.PersistedSnapshotCount < 2) return;

        // Window left edge is the raw block number (blockNumber - size); the alignment lives in
        // offset-shifted space, so ((blockNumber-1)/size)*size would only be correct at offset 0.
        // Clamped to the persistence point: snapshots below the persisted block are already in RocksDB,
        // so merging them is wasted work. The clamp also makes the assemble walk reject a below-persistence
        // large-compacted skip-pointer (whose To is above the persisted block but whose From is below it)
        // and instead assemble from the persisted block upward via narrower edges. A no-op when
        // persistedBlockNumber <= blockNumber - size.
        long startingBlockNumber = Math.Max(blockNumber - size, persistedBlockNumber);
        CompactRange(snapshotTo, startingBlockNumber, size, isCompactSized: false);
    }

    /// <summary>
    /// Produce the <c>CompactSize</c>-wide snapshot ending at the boundary
    /// block <paramref name="snapshotTo"/> — the snapshot <c>PersistenceManager</c> writes to
    /// RocksDB. Invoked by the background batch worker (see <see cref="Enqueue"/>); not part of
    /// <see cref="IPersistedSnapshotCompactor"/>.
    /// </summary>
    public void DoCompactCompactSized(StateId snapshotTo)
    {
        long blockNumber = snapshotTo.BlockNumber;
        if (!_schedule.IsCompactSizeBoundary(blockNumber) && !_schedule.IsLargeCompactionBoundary(blockNumber)) return;

        if (snapshotRepository.PersistedSnapshotCount < 2) return;

        // The CompactSized snapshot is always CompactSize-wide; GetCompactSize returns exactly CompactSize at
        // any boundary (it caps there), so the window is (blockNumber - CompactSize, blockNumber]. No
        // persistence clamp: this CompactSize-wide window lands on a persistence boundary and never dips
        // below the persisted block.
        int compactSize = _schedule.GetCompactSize(blockNumber);
        CompactRange(snapshotTo, blockNumber - compactSize, compactSize, isCompactSized: true);
    }

    private bool CompactRange(StateId snapshotTo, long startingBlockNumber, int compactSize, bool isCompactSized)
    {
        using PersistedSnapshotList snapshots = snapshotRepository.AssemblePersistedSnapshotsForCompaction(snapshotTo, startingBlockNumber);
        if (snapshots.Count < 2) return false;

        if (_logger.IsDebug) _logger.Debug($"Compacting {snapshots.Count} persisted snapshots at block {snapshotTo.BlockNumber}, compact size {compactSize}, CompactSized {isCompactSized}");

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
        try
        {
            long estimatedSize = 0;
            long bloomCapacity = 0;
            // A large compaction adopts one bloom across the snapshots it contains, so the assembled
            // sources can share a single filter that already reports the whole window's key count.
            // Dedup by owner so a shared bloom is counted once instead of once per source — otherwise
            // bloomCapacity (and the merged filter) is inflated by the number of sharers.
            HashSet<RefCountedBloomFilter> countedBlooms = [];
            for (int i = 0; i < n; i++)
            {
                // Session dispose madvises the source's mmap range cold — the compacted
                // snapshot that supersedes these sources warms its own cache lazily on the
                // first read of each address, so there's no value in keeping these pages.
                sessionsList[i] = snapshots[i].BeginWholeReadSession();

                estimatedSize += snapshots[i].Size;
                // Each source carries its own bloom; sum their key counts to size the merge.
                // The AlwaysTrue placeholder reports Count == 0, so a not-yet-built source just
                // contributes nothing — same as the old manager's sentinel did.
                if (countedBlooms.Add(snapshots[i].BloomRef))
                    bloomCapacity += snapshots[i].Bloom.Count;
            }

            // Bloom-disabled or empty-capacity case uses an AlwaysTrue sentinel so the
            // downstream AddCompactedSnapshot receives a non-null bloom uniformly.
            BloomFilter mergedBloom = _bloomBitsPerKey > 0 && bloomCapacity > 0
                ? new BloomFilter(bloomCapacity, _bloomBitsPerKey)
                : BloomFilter.AlwaysTrue();
            // A non-CompactSized merge at a large-compaction boundary spans >CompactSize — its own tier
            // so the assemble walk can prefer it as the widest skip-pointer. Computed up front so the
            // sub-CompactSize tier (PersistedSmallCompacted) lands in the separate small-arena pool.
            SnapshotTier tier = isCompactSized
                ? SnapshotTier.PersistedCompactSized
                : _schedule.IsLargeCompactionBoundary(snapshotTo.BlockNumber)
                    ? SnapshotTier.PersistedLargeCompacted
                    : SnapshotTier.PersistedSmallCompacted;

            SnapshotLocation location;
            ArenaReservation reservation;
            using (ArenaWriter arenaWriter = arenaManager.CreateWriter(estimatedSize, small: tier == SnapshotTier.PersistedSmallCompacted))
            {
                long sw = Stopwatch.GetTimestamp();
                PersistedSnapshotMerger.NWayMergeSnapshots<ArenaBufferWriter, WholeReadSession, WholeReadSessionReader, NoOpPin>(
                    sessionsList.AsSpan(), ref arenaWriter.GetWriter(), mergedBloom);

                long len = arenaWriter.GetWriter().Written;
                // The assembled window is best-effort and may fall short of compactSize, so label by the
                // actual compacted block span rounded up to the next power of two, not the target size.
                int actualSize = (int)BitOperations.RoundUpToPowerOf2((ulong)(to.BlockNumber - from.BlockNumber));
                CompactSizeLabel sizeLabel = new(actualSize);
                Metrics.PersistedSnapshotCompactedSize.Observe(len, sizeLabel);
                Metrics.PersistedSnapshotCompactTime.Observe(Stopwatch.GetTimestamp() - sw, sizeLabel);

                (location, reservation) = arenaWriter.Complete();
            }

            // Durability barrier — fsync the metadata arena before the catalog records the
            // compacted entry. No blob fsync here: compaction does not write new blobs, it
            // only emits NodeRefs into existing base blob arenas (those were fsynced when
            // their respective base snapshots were converted).
            reservation.Fsync();

            _catalog.Add(new CatalogEntry(from, to, location, tier));
            using (PersistedSnapshot compacted = new(from, to, reservation, blobs, tier, new RefCountedBloomFilter(mergedBloom)))
            {
                reservation.Dispose();
                snapshotRepository.AddPersistedSnapshot(compacted, tier);
                if (!_schedule.IsCompactSizeBoundary(snapshotTo.BlockNumber) && !_schedule.IsLargeCompactionBoundary(snapshotTo.BlockNumber))
                {
                    // Sub-CompactSize intermediate. The bundle priority means this is never queried
                    // unless there's a deep reorg, so drop its freshly-written pages from the cache +
                    // tracker; they would otherwise sit hot until the snapshot is pruned.
                    compacted.Demote();
                }
                else
                {
                    WarmAddressColumnIndex(compacted);
                    // A >CompactSize merge spans (from, to] on the canonical chain, so its bloom is a
                    // superset pre-filter for every persisted snapshot fully contained there. Adopt it
                    // across all of them — each then shares one bloom and frees its own (multi-MiB)
                    // filter, while still pre-filtering (unlike the AlwaysTrue demote sentinel).
                    if (_schedule.IsLargeCompactionBoundary(snapshotTo.BlockNumber))
                        snapshotRepository.ShareBloomAcrossRange(from, to, compacted.BloomRef, blobs);
                }
            }

            Metrics.PersistedSnapshotCompactions++;
            return true;
        }
        finally
        {
            for (int i = 0; i < n; i++) sessionsList[i]?.Dispose();
        }
    }

    /// <summary>
    /// Pre-fault the sorted table's offset region (the binary-search index at the tail of a
    /// freshly-written large-tier snapshot) so it lands in the page-residency tracker. Without
    /// this, the first lookups take a chain of inline minor page faults walking the offsets.
    /// </summary>
    internal static void WarmAddressColumnIndex(PersistedSnapshot snapshot)
    {
        ArenaReservation reservation = snapshot.Reservation;
        ArenaByteReader reader = reservation.CreateReader();
        Bound table = new(0, reader.Length);
        if (!SortedTable.TryReadFooter<ArenaByteReader, NoOpPin>(in reader, table, out _, out _, out long offsetRegionStart))
            return;

        // The reader is reservation-relative, and TouchRangePopulate takes reservation-relative
        // offsets, so offsetRegionStart maps directly. The warmed range covers the offset array
        // plus the footer up to the table end.
        long indexLen = table.Length - offsetRegionStart;
        if (indexLen <= 0) return;
        reservation.TouchRangePopulate(offsetRegionStart, indexLen);
    }
}
