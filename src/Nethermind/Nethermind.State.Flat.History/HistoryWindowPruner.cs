// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>A per-pass work budget, checked once per scanned row. <see cref="HistoryWindowPruner"/> uses a real
/// wall-clock budget in production; tests inject a deterministic implementation instead of racing one, per the
/// project's no-timing-tests rule.</summary>
internal interface IPruneBudget
{
    bool Exhausted { get; }
}

/// <summary>Forces a scan to yield before examining its first row — deterministic stand-in for a wall-clock
/// budget that expires immediately, without depending on how fast the scan happens to run.</summary>
internal sealed class AlreadyExhaustedBudget : IPruneBudget
{
    public static readonly AlreadyExhaustedBudget Instance = new();

    private AlreadyExhaustedBudget() { }

    public bool Exhausted => true;
}

/// <summary>
/// Reclaims disk for a bounded rolling window: as the watermark advances, keeps only the newest version of each
/// key at or below the new floor (everything an as-of read between the floor and the watermark still needs) and
/// deletes strictly older versions. A single pass is a bounded, resumable scan-and-delete over each history
/// column — never a whole-column compaction — so it never blocks capture (which only ever writes above the
/// watermark; the pruner only ever deletes below the floor, a disjoint range by construction) and never blocks a
/// concurrent backfill importer from running (the two are mutually exclusive via <see cref="IBackfillInterlock"/>).
/// </summary>
/// <remarks>
/// A RocksDB compaction filter expressing "drop everything below the floor for a key, keep the first at/below it"
/// natively during compaction is the natural long-term primitive for this — the predicate is stateful only within
/// a key's contiguous run, which a filter sees in order for free. That binding does not exist in this repo's
/// RocksDB wrapper today, so this pass implements the same predicate as an explicit application-level scan. The
/// column-store abstraction here (bounded scan, persisted cursor, swap for a filter-backed strategy later) is the
/// seam a future change would replace, not this class's public surface.
/// </remarks>
public sealed class HistoryWindowPruner : IDisposable
{
    private const int BlockBytes = sizeof(ulong);
    private const int FlushEveryNDeletes = 1000;

    private static ReadOnlySpan<byte> AccountCursorKey => "history:prune:cursor:account"u8;
    private static ReadOnlySpan<byte> StorageCursorKey => "history:prune:cursor:storage"u8;
    private static ReadOnlySpan<byte> ClearsCursorKey => "history:prune:cursor:clears"u8;
    private static ReadOnlySpan<byte> BlocksCursorKey => "history:prune:cursor:blocks"u8;

    private readonly HistoryWriter _writer;
    private readonly IDb _availableBlocks;
    private readonly IDb _accountHistory;
    private readonly IDb _storageHistory;
    private readonly IDb _storageClears;
    private readonly HistoryAvailability _availability;
    private readonly IFlatDbConfig _config;
    private readonly IBackfillInterlock _interlock;
    private readonly HistoryScopeGate _scopeGate;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private ulong _lastFloorPublishWatermark;

    // Owned here (not in HistoryAvailability, a pure availability/scope-record store) so a concurrent backfill
    // importer has one dedicated method pair to call around its active pass. Real mutual exclusion, not a sampled
    // flag: RunOnePass only proceeds if it can claim the gate outright, so a pass can never start deleting rows a
    // backfill is concurrently writing, and BeginBackfill only returns once any in-flight pass has released it.
    // Plain-lock-backed (not ReaderWriterLockSlim): ImportRangeAsync awaits across network I/O between
    // BeginBackfill and its matching dispose, and its continuation can resume on a different thread — a lock type
    // that tracks per-thread ownership would throw when release happens on a thread that never acquired it.
    private readonly BackfillGate _backfillGate = new();

    public HistoryWindowPruner(
        HistoryWriter writer,
        IColumnsDb<FlatHistoryColumns> history,
        IFlatDbConfig config,
        IBackfillInterlock interlock,
        HistoryScopeGate scopeGate,
        ILogManager logManager)
    {
        _writer = writer;
        _availableBlocks = history.GetColumnDb(FlatHistoryColumns.AvailableBlocks);
        _accountHistory = history.GetColumnDb(FlatHistoryColumns.AccountHistory);
        _storageHistory = history.GetColumnDb(FlatHistoryColumns.StorageHistory);
        _storageClears = history.GetColumnDb(FlatHistoryColumns.StorageClears);
        _availability = new HistoryAvailability(_availableBlocks);
        _config = config;
        _interlock = interlock;
        _scopeGate = scopeGate;
        _logger = logManager.GetClassLogger<HistoryWindowPruner>();

        _loop = config.HistoryRetentionBlocks > 0 ? RunLoopAsync() : Task.CompletedTask;
        if (config.HistoryRetentionBlocks > 0)
        {
            writer.WatermarkAdvanced += OnWatermarkAdvanced;
        }
    }

    /// <summary>Blocks (briefly — only against an already-in-flight prune pass, bounded by its own budget) until
    /// the gate is claimed for backfill, then marks it active for the duration of the returned scope. Multiple
    /// concurrent backfills may hold this simultaneously (many readers); a prune pass claims it exclusively (one
    /// writer, no readers). Idempotent and exception-safe by construction — disposing the same scope twice, or
    /// disposing it from a <c>finally</c> after an exception, only ever releases once.</summary>
    public IDisposable BeginBackfill()
    {
        SpinWait spinner = default;
        while (!_backfillGate.TryEnterBackfill())
        {
            spinner.SpinOnce();
        }

        return new BackfillScope(this);
    }

    private void EndBackfill() => _backfillGate.ExitBackfill();

    private sealed class BackfillScope(HistoryWindowPruner owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.EndBackfill();
        }
    }

    /// <summary>Real shared/exclusive gate between concurrent backfill importers (any number of simultaneous
    /// readers) and this pruner (one exclusive writer, admitted only when no reader holds the gate). Deliberately
    /// not <see cref="ReaderWriterLockSlim"/>: that type tracks lock ownership per-thread, and
    /// <c>BeginBackfill</c>/<c>EndBackfill</c> are held across <c>await</c> points in an async importer whose
    /// continuation can resume on a different thread than the one that entered — released-without-held would
    /// throw. This type carries no thread affinity: each method is a single synchronous, uncontended-fast
    /// <c>lock</c> section, never held across a suspension point.</summary>
    private sealed class BackfillGate
    {
        private readonly Lock _sync = new();
        private int _activeBackfills;
        private bool _pruningActive;

        public bool TryEnterBackfill()
        {
            lock (_sync)
            {
                if (_pruningActive) return false;
                _activeBackfills++;
                return true;
            }
        }

        public void ExitBackfill()
        {
            lock (_sync)
            {
                _activeBackfills--;
            }
        }

        public bool TryEnterPrune()
        {
            lock (_sync)
            {
                if (_activeBackfills > 0 || _pruningActive) return false;
                _pruningActive = true;
                return true;
            }
        }

        public void ExitPrune()
        {
            lock (_sync)
            {
                _pruningActive = false;
            }
        }
    }

    private void OnWatermarkAdvanced(ulong watermark)
    {
        // Written from RunOnePassUnderGate on the prune loop's own task, read here from whatever thread the
        // writer's capture path runs on: Volatile is the correctness requirement, not the plain field a
        // single-writer/single-reader assumption would permit.
        if (watermark < Volatile.Read(ref _lastFloorPublishWatermark) + _config.HistoryPruneIntervalBlocks) return;
        try { _wakeSignal.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task RunLoopAsync()
    {
        CancellationToken token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _wakeSignal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // No pre-check against the gate here: RunOnePass claims it (or cleanly declines) itself, so there is
            // exactly one place that can skip a pass — a duplicate outer check previously consumed the wake
            // signal on decline without re-arming it, losing the very request that should have retried later.
            try
            {
                RunOnePass(token);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (_logger.IsError) _logger.Error("A history window pruner pass failed.", e);
            }
        }
    }

    /// <summary>Runs one prune cycle synchronously on the calling thread — internal so tests can drive it
    /// directly instead of racing the background wake-signal loop, per the project's no-timing-tests rule.
    /// <paramref name="budgetFactory"/> lets a test supply a deterministic budget instead of racing a wall-clock
    /// <see cref="HistoryPrunePassBudgetSeconds"/> value — called once per column, exactly like the production
    /// path constructs one <see cref="WallClockBudget"/> per column, so a stateful test budget for one column
    /// (e.g. "exhausted after N rows") never leaks its state into an unrelated column.</summary>
    internal void RunOnePass(CancellationToken token, Func<IPruneBudget>? budgetFactory = null)
    {
        // Advisory fast path: some other caller may set this without ever going through BeginBackfill/EndBackfill.
        // Declining here (or at the real gate below) never re-arms the wake signal immediately — doing so would
        // busy-spin RunLoopAsync for the entire backfill duration, since the very next iteration would just wake,
        // decline, and re-arm again. A decline instead relies on OnWatermarkAdvanced's own natural retrigger:
        // capture keeps advancing the watermark during backfill, and each capture re-evaluates the interval
        // threshold against it, so the request is retried on the next captured block rather than lost outright.
        if (_interlock.IsBackfillActive) return;
        if (!_backfillGate.TryEnterPrune()) return;

        try
        {
            RunOnePassUnderGate(token, budgetFactory);
        }
        finally
        {
            _backfillGate.ExitPrune();
        }
    }

    private void RunOnePassUnderGate(CancellationToken token, Func<IPruneBudget>? budgetFactory)
    {
        ulong retention = _config.HistoryRetentionBlocks;
        if (retention == 0) return;

        // Never zero: a zero-duration wall-clock budget would exhaust before scanning a single row, forever
        // (HasAnyPendingCursor then stays true, so the floor can never advance again either).
        TimeSpan passBudget = TimeSpan.FromSeconds(Math.Max(1, _config.HistoryPrunePassBudgetSeconds));

        ulong floor;
        if (HasAnyPendingCursor())
        {
            // A previous pass yielded mid-column for the already-published floor: resume that work. Computing
            // and comparing a fresh floor here would see "no advance" (the floor was already published) and skip
            // the pass entirely, silently abandoning the deletes it still owes for the current window.
            if (!_availability.TryGetGlobalFloor(out floor)) return;
        }
        else
        {
            ulong watermark = _writer.LastCapturedBlock;
            if (watermark <= retention) return;

            ulong newFloor = watermark - retention;
            _availability.TryGetGlobalFloor(out ulong currentFloor);
            if (newFloor <= currentFloor) return;

            // Floor publishes before the drain (and before any delete): a scope opened after this point already
            // sees the new floor at its own admission check, so it is safe by construction regardless of which
            // epoch it lands in — only scopes admitted under the old, lower floor need draining before deleting.
            _availability.PublishGlobalFloor(newFloor);
            Metrics.FlatHistoryFloor = (long)newFloor;
            Volatile.Write(ref _lastFloorPublishWatermark, watermark);
            floor = newFloor;

            if (!_scopeGate.TryDrainForFloorAdvance(passBudget, token))
            {
                if (_logger.IsWarn) _logger.Warn(
                    "History window pruner published a new floor but historical read scopes opened before it did not drain within the budget; deletes for this floor are deferred to the next pass.");
                return;
            }
        }

        // Each column gets its own budget instance so a slow account column can never starve storage/clears/markers
        // of all progress: without this, a resumed pass would always restart from "has account finished yet?" and
        // the other three columns could go passes on end without a single row examined.
        Func<IPruneBudget> newBudget = budgetFactory ?? (() => new WallClockBudget(passBudget));
        bool completedAccount = PruneVersionedColumn(_accountHistory, AccountCursorKey, BaseFlatPersistence.AccountKeyLength, floor, newBudget(), token);
        bool completedStorage = PruneVersionedColumn(_storageHistory, StorageCursorKey, BaseFlatPersistence.StorageKeyLength, floor, newBudget(), token);
        bool completedClears = PruneClearsColumn(floor, newBudget(), token);
        bool completedBlocks = PruneBlockMarkers(floor, newBudget(), token);

        if (!(completedAccount && completedStorage && completedClears && completedBlocks))
        {
            Metrics.FlatHistoryPrunePassesYielded++;
            try { _wakeSignal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    /// <summary>
    /// Descending-suffix columns (<c>AccountHistory</c>, <c>StorageHistory</c>): each key's versions sit
    /// contiguously newest-first. Per key, the first row at or below the floor is the retained answer for every
    /// read in <c>[floor, next-change)</c>; every row after it in the same run (strictly older) is dead weight.
    /// </summary>
    private bool PruneVersionedColumn(IDb column, ReadOnlySpan<byte> cursorKeyName, int flatKeyLength, ulong floor, IPruneBudget budget, CancellationToken token)
    {
        ISortedKeyValueStore sorted = (ISortedKeyValueStore)column;
        byte[]? cursor = ReadCursor(cursorKeyName);

        Span<byte> upperBound = stackalloc byte[flatKeyLength + BlockBytes + 1];
        upperBound.Fill(0xFF);

        byte[]? currentGroupKey = null;
        bool currentGroupHasFloorRow = false;
        int sinceFlush = 0;

        using ISortedView view = sorted.GetViewBetween(cursor ?? ReadOnlySpan<byte>.Empty, upperBound);
        IWriteBatch batch = column.StartWriteBatch();
        try
        {
            while (view.MoveNext())
            {
                if (budget.Exhausted || token.IsCancellationRequested)
                {
                    WriteCursor(cursorKeyName, view.CurrentKey);
                    return false;
                }

                ReadOnlySpan<byte> key = view.CurrentKey;
                if (key.Length != flatKeyLength + BlockBytes) continue;

                ReadOnlySpan<byte> keyPrefix = key[..flatKeyLength];
                if (currentGroupKey is null || !keyPrefix.SequenceEqual(currentGroupKey))
                {
                    currentGroupKey = keyPrefix.ToArray();
                    currentGroupHasFloorRow = false;
                }

                ulong block = ~BinaryPrimitives.ReadUInt64BigEndian(key[flatKeyLength..]);
                if (block <= floor)
                {
                    if (currentGroupHasFloorRow)
                    {
                        batch.Remove(key);
                        Metrics.FlatHistoryPrunedRows++;
                        sinceFlush = FlushBatchIfNeeded(column, ref batch, sinceFlush);
                    }
                    else
                    {
                        currentGroupHasFloorRow = true;
                    }
                }
            }
        }
        finally
        {
            batch.Dispose();
        }

        ClearCursor(cursorKeyName);
        return true;
    }

    /// <summary>
    /// Ascending-suffix column (<c>StorageClears</c>, <c>[accountKey | block]</c>): per account, keeps every clear
    /// at or above the floor plus the single newest clear below it (the one a floor read still needs to
    /// distinguish a dead slot from a live one); an even newer below-floor clear for the same account supersedes
    /// and deletes whichever below-floor clear was previously held for it.
    /// </summary>
    private bool PruneClearsColumn(ulong floor, IPruneBudget budget, CancellationToken token)
    {
        const int accountKeyLength = BaseFlatPersistence.AccountKeyLength;
        ISortedKeyValueStore sorted = (ISortedKeyValueStore)_storageClears;
        byte[]? cursor = ReadCursor(ClearsCursorKey);

        Span<byte> upperBound = stackalloc byte[accountKeyLength + BlockBytes + 1];
        upperBound.Fill(0xFF);

        byte[]? pendingBelowFloorKey = null;
        int sinceFlush = 0;

        using ISortedView view = sorted.GetViewBetween(cursor ?? ReadOnlySpan<byte>.Empty, upperBound);
        IWriteBatch batch = _storageClears.StartWriteBatch();
        try
        {
            while (view.MoveNext())
            {
                if (budget.Exhausted || token.IsCancellationRequested)
                {
                    WriteCursor(ClearsCursorKey, view.CurrentKey);
                    return false;
                }

                ReadOnlySpan<byte> key = view.CurrentKey;
                if (key.Length != accountKeyLength + BlockBytes) continue;

                ulong block = BinaryPrimitives.ReadUInt64BigEndian(key[accountKeyLength..]);
                if (block < floor)
                {
                    if (pendingBelowFloorKey is not null && key[..accountKeyLength].SequenceEqual(((ReadOnlySpan<byte>)pendingBelowFloorKey)[..accountKeyLength]))
                    {
                        batch.Remove(pendingBelowFloorKey);
                        Metrics.FlatHistoryPrunedRows++;
                        sinceFlush = FlushBatchIfNeeded(_storageClears, ref batch, sinceFlush);
                    }

                    pendingBelowFloorKey = key.ToArray();
                }
            }
        }
        finally
        {
            batch.Dispose();
        }

        ClearCursor(ClearsCursorKey);
        return true;
    }

    /// <summary>Per-block availability markers need no per-key retention logic: any marker strictly below the
    /// floor is unconditionally dead (a legal capture connect point never needs to verify below the floor).</summary>
    private bool PruneBlockMarkers(ulong floor, IPruneBudget budget, CancellationToken token)
    {
        ISortedKeyValueStore sorted = (ISortedKeyValueStore)_availableBlocks;
        byte[]? cursor = ReadCursor(BlocksCursorKey);

        Span<byte> upperBound = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(upperBound, floor);

        int sinceFlush = 0;
        using ISortedView view = sorted.GetViewBetween(cursor ?? ReadOnlySpan<byte>.Empty, upperBound);
        IWriteBatch batch = _availableBlocks.StartWriteBatch();
        try
        {
            while (view.MoveNext())
            {
                if (budget.Exhausted || token.IsCancellationRequested)
                {
                    WriteCursor(BlocksCursorKey, view.CurrentKey);
                    return false;
                }

                ReadOnlySpan<byte> key = view.CurrentKey;
                if (key.Length != BlockBytes) continue; // reserved (non-block) keys are longer; never touched

                batch.Remove(key);
                Metrics.FlatHistoryPrunedRows++;
                sinceFlush = FlushBatchIfNeeded(_availableBlocks, ref batch, sinceFlush);
            }
        }
        finally
        {
            batch.Dispose();
        }

        ClearCursor(BlocksCursorKey);
        return true;
    }

    private static int FlushBatchIfNeeded(IDb column, ref IWriteBatch batch, int sinceFlush)
    {
        if (++sinceFlush < FlushEveryNDeletes) return sinceFlush;

        batch.Dispose();
        batch = column.StartWriteBatch();
        return 0;
    }

    private bool HasAnyPendingCursor() =>
        _availableBlocks.Get(AccountCursorKey) is not null ||
        _availableBlocks.Get(StorageCursorKey) is not null ||
        _availableBlocks.Get(ClearsCursorKey) is not null ||
        _availableBlocks.Get(BlocksCursorKey) is not null;

    private byte[]? ReadCursor(ReadOnlySpan<byte> cursorKeyName) => _availableBlocks.Get(cursorKeyName);

    private void WriteCursor(ReadOnlySpan<byte> cursorKeyName, ReadOnlySpan<byte> key) => _availableBlocks.PutSpan(cursorKeyName, key);

    private void ClearCursor(ReadOnlySpan<byte> cursorKeyName) => _availableBlocks.Remove(cursorKeyName);

    public void Dispose()
    {
        if (_config.HistoryRetentionBlocks > 0)
        {
            _writer.WatermarkAdvanced -= OnWatermarkAdvanced;
        }

        _cts.Cancel();
        try
        {
            _loop.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }

        _cts.Dispose();
        _wakeSignal.Dispose();
    }

    private sealed class WallClockBudget(TimeSpan budget) : IPruneBudget
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public bool Exhausted => _stopwatch.Elapsed >= budget;
    }
}
