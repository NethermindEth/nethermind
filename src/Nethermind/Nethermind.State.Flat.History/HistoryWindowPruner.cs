// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
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

/// <summary>
/// Reclaims disk for a bounded rolling window: as the watermark advances, keeps only the row(s) each key's
/// row format requires for reads in <c>[floor, watermark]</c> to keep resolving, and deletes the rest. A single
/// pass is a bounded, resumable scan-and-delete over each history column — never a whole-column compaction — so
/// it never blocks capture (which only ever writes above the watermark; the pruner only ever deletes below the
/// floor, a disjoint range by construction) and never blocks a concurrent backfill importer from running (the two
/// are mutually exclusive via <see cref="IBackfillInterlock"/>).
/// </summary>
public sealed class HistoryWindowPruner : IDisposable
{
    private const int BlockBytes = sizeof(ulong);
    private const int FlushEveryNDeletes = 1000;

    private static ReadOnlySpan<byte> AccountCursorKey => "history:prune:cursor:account"u8;
    private static ReadOnlySpan<byte> StorageCursorKey => "history:prune:cursor:storage"u8;
    private static ReadOnlySpan<byte> ClearsCursorKey => "history:prune:cursor:clears"u8;
    private static ReadOnlySpan<byte> BlocksCursorKey => "history:prune:cursor:blocks"u8;
    private static ReadOnlySpan<byte> SidecarRetentionCursorKey => "history:prune:cursor:sidecar-retention"u8;
    private static ReadOnlySpan<byte> SidecarOverCapCursorKey => "history:prune:cursor:sidecar-overcap"u8;

    // Rows checked between each GatherMetric() re-check while purging over the byte cap - cheap enough to call
    // often, but calling it after every single delete would be needless overhead on a column that can be huge.
    private const int SidecarOverCapMetricCheckInterval = 1000;

    private readonly HistoryWriter _writer;
    private readonly IDb _availableBlocks;
    private readonly IDb _accountHistory;
    private readonly IDb _storageHistory;
    private readonly IDb _storageClears;
    private readonly IDb _changesetSidecar;
    private readonly HistoryAvailability _availability;
    private readonly HistoryRowFormat _rowFormat;
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
        HistoryAvailability availability,
        HistoryRowFormat rowFormat,
        ILogManager logManager)
    {
        _writer = writer;
        _availableBlocks = history.GetColumnDb(FlatHistoryColumns.AvailableBlocks);
        _accountHistory = history.GetColumnDb(FlatHistoryColumns.AccountHistory);
        _storageHistory = history.GetColumnDb(FlatHistoryColumns.StorageHistory);
        _storageClears = history.GetColumnDb(FlatHistoryColumns.StorageClears);
        _changesetSidecar = history.GetColumnDb(FlatHistoryColumns.ChangesetSidecar);
        _availability = availability;
        _rowFormat = rowFormat;
        _config = config;
        _interlock = interlock;
        _scopeGate = scopeGate;
        _logger = logManager.GetClassLogger<HistoryWindowPruner>();

        // The sidecar's own retention/cap can require pruning even when the read-path window is unbounded
        // (HistoryRetentionBlocks == 0) - the two are independent knobs, see PruneChangesetSidecarColumn's remarks.
        bool shouldRun = config.HistoryRetentionBlocks > 0 || SidecarPruningConfigured(config);
        _loop = shouldRun ? RunLoopAsync() : Task.CompletedTask;
        if (shouldRun)
        {
            writer.WatermarkAdvanced += OnWatermarkAdvanced;
        }
    }

    private static bool SidecarPruningConfigured(IFlatDbConfig config) =>
        config.HistoryChangesetSidecarEnabled &&
        (config.HistoryChangesetSidecarRetentionBlocks > 0 || config.HistoryChangesetSidecarMaxBytes > 0);

    /// <summary>
    /// One-time (startup) reconciliation of the operator's <c>Flat.HistorySliceAddresses</c> allow-list against
    /// the scope records already on disk: deletes a scope for an address no longer configured (its rows below the
    /// general floor become prunable again on the next pass) and creates one for a newly configured address that
    /// has none yet. Never touches an already-existing scope's floor - that is the per-pass maintenance below
    /// (bounded retention) or a future backfill importer's job (unbounded), never a blind reset back to some seed
    /// value on every restart.
    /// </summary>
    /// <exception cref="InvalidConfigurationException">Slices are configured against a database that is not
    /// windowed (<see cref="IFlatDbConfig.HistoryRetentionBlocks"/> is 0) - a slice is meaningless there (the v2
    /// unwindowed format already retains everything), and publishing one would incorrectly stamp the windowed
    /// format onto v2 data.</exception>
    public void ReconcileSliceScopes()
    {
        IReadOnlyList<SliceScopeEntry> configured = SliceScopeConfig.Parse(_config.HistorySliceAddresses);

        if (configured.Count > 0 && !_rowFormat.IsV3)
        {
            throw new InvalidConfigurationException(
                "Flat.HistorySliceAddresses is set, but this flatHistory database is not windowed " +
                "(HistoryRetentionBlocks is 0). Per-contract slices require the v3 pre-value format used by " +
                "windowed retention; unset HistorySliceAddresses or set HistoryRetentionBlocks.", -1);
        }

        Dictionary<byte[], SliceScopeEntry> configuredByKey = new(configured.Count, Bytes.EqualityComparer);
        foreach (SliceScopeEntry entry in configured)
        {
            configuredByKey[AccountKeyOf(entry.Address)] = entry;
        }

        // Runs even for an empty configured list, so removing the last remaining slice from the allow-list still
        // deletes its scope record instead of leaving it orphaned on disk.
        foreach (ScopeFloor existing in _availability.GetScopes())
        {
            if (!configuredByKey.ContainsKey(existing.Key))
            {
                _availability.RemoveScope(existing.Key);
            }
        }

        if (configured.Count == 0) return;

        _availability.TryGetGlobalFloor(out ulong currentGeneralFloor);
        ulong watermark = _writer.LastCapturedBlock;

        foreach ((byte[] key, SliceScopeEntry entry) in configuredByKey)
        {
            if (_availability.TryGetScopeFloor(key, out _)) continue;

            ulong seedFloor = currentGeneralFloor;
            if (entry.RetentionBlocks is { } retention)
            {
                ulong retentionFloor = watermark > retention ? watermark - retention : 0;
                seedFloor = Math.Max(currentGeneralFloor, retentionFloor);
            }

            _availability.PublishScope(key, seedFloor);
        }
    }

    /// <summary>Per-pass maintenance for a slice configured with a bounded (not unbounded) retention: advances its
    /// own floor toward <c>watermark - retention</c> as the watermark grows, the same way the general floor
    /// advances - never past its own current value (<see cref="HistoryAvailability.TryRaiseScopeFloor"/> is a
    /// raise-only CAS), and never below <see cref="ReconcileSliceScopes"/>'s seed.</summary>
    private void MaintainBoundedSliceFloors(ulong watermark)
    {
        foreach (SliceScopeEntry entry in SliceScopeConfig.Parse(_config.HistorySliceAddresses))
        {
            if (entry.RetentionBlocks is not { } retention || watermark <= retention) continue;
            _availability.TryRaiseScopeFloor(AccountKeyOf(entry.Address), watermark - retention);
        }
    }

    private static byte[] AccountKeyOf(Address address) =>
        address.ToAccountPath.Bytes[..BaseFlatPersistence.AccountKeyLength].ToArray();

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

    /// <summary>Async counterpart to <see cref="BeginBackfill"/>: suspends instead of spinning while an in-flight
    /// prune pass holds the gate. A pass can run for its whole configured budget
    /// (<see cref="IFlatDbConfig.HistoryPrunePassBudgetSeconds"/>, up to tens of seconds), so a caller reached from
    /// an async import loop should never burn a thread spin-waiting that long — this awaits the pass's exit signal
    /// instead. Same acquire/release contract as <see cref="BeginBackfill"/> otherwise: idempotent, exception-safe
    /// scope, any number of concurrent backfills admitted, exclusive against a prune pass.</summary>
    public async Task<IDisposable> BeginBackfillAsync(CancellationToken cancellationToken)
    {
        while (!_backfillGate.TryEnterBackfill())
        {
            await _backfillGate.WaitForPruneToExitAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
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
        // Reset to a fresh instance on every ExitPrune: an awaiter that captured the Task before this pass
        // started must still observe it complete when this exact pass exits, even though a later pass may already
        // be waiting on the replacement instance by then.
        private TaskCompletionSource<bool> _pruneExited = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        /// <summary>The Task an async waiter should await before retrying <see cref="TryEnterBackfill"/> - already
        /// completed when no pass is currently active, so a caller can unconditionally await this exactly once per
        /// failed <see cref="TryEnterBackfill"/> attempt without a separate active-check.</summary>
        public Task WaitForPruneToExitAsync()
        {
            lock (_sync)
            {
                return _pruningActive ? _pruneExited.Task : Task.CompletedTask;
            }
        }

        public void ExitPrune()
        {
            TaskCompletionSource<bool> exited;
            lock (_sync)
            {
                _pruningActive = false;
                exited = _pruneExited;
                _pruneExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            exited.TrySetResult(true);
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
        // Never zero: a zero-duration wall-clock budget would exhaust before scanning a single row, forever
        // (HasAnyPendingCursor then stays true, so the floor can never advance again either).
        TimeSpan passBudget = TimeSpan.FromSeconds(Math.Max(1, _config.HistoryPrunePassBudgetSeconds));
        Func<IPruneBudget> newBudget = budgetFactory ?? (() => new WallClockBudget(passBudget));

        // Independent of the read-path window below: the sidecar has its own retention knob and hard byte cap,
        // and no reader ever pins against it through HistoryScopeGate, so it needs neither a floor-publish nor a
        // drain before deleting - only a plain, resumable range scan.
        bool completedSidecar = PruneChangesetSidecarColumn(newBudget, token);

        bool completedReadPathWindow = RunReadPathWindowPass(passBudget, newBudget, token);

        if (!(completedSidecar && completedReadPathWindow))
        {
            Metrics.FlatHistoryPrunePassesYielded++;
            try { _wakeSignal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    /// <summary>The read-path window: bounds what an as-of-block read can still resolve, so a floor advance must
    /// publish before draining scopes admitted under the old floor and before any delete. Returns whether this
    /// pass finished the whole window (all four columns), not just started it.</summary>
    private bool RunReadPathWindowPass(TimeSpan passBudget, Func<IPruneBudget> newBudget, CancellationToken token)
    {
        ulong retention = _config.HistoryRetentionBlocks;
        if (retention == 0) return true;

        ulong floor;
        if (HasAnyPendingCursor())
        {
            // A previous pass yielded mid-column for the already-published floor: resume that work. Computing
            // and comparing a fresh floor here would see "no advance" (the floor was already published) and skip
            // the pass entirely, silently abandoning the deletes it still owes for the current window.
            if (!_availability.TryGetGlobalFloor(out floor)) return true;
        }
        else
        {
            ulong watermark = _writer.LastCapturedBlock;
            MaintainBoundedSliceFloors(watermark);
            if (watermark <= retention) return true;

            ulong newFloor = watermark - retention;

            // A backfill importer may have connected a range that extends the window further back than this
            // pass's own retention math would compute; raising the floor past its bottom without pruning it in
            // the same pass would silently re-delete data the importer just spent effort populating.
            if (_availability.TryGetConnectedRange(out ulong importedFloor, out _) && importedFloor < newFloor)
            {
                newFloor = importedFloor;
            }

            // CAS-style: only actually raises if newFloor is still above whatever the current value is at this
            // instant, so a concurrent importer lowering the floor on the same shared instance cannot have its
            // write clobbered by this call proceeding on a stale read.
            if (!_availability.TryRaiseGlobalFloor(newFloor)) return true;

            Metrics.FlatHistoryFloor = (long)newFloor;
            Volatile.Write(ref _lastFloorPublishWatermark, watermark);
            floor = newFloor;

            // Floor publishes before the drain (and before any delete): a scope opened after this point already
            // sees the new floor at its own admission check, so it is safe by construction regardless of which
            // epoch it lands in — only scopes admitted under the old, lower floor need draining before deleting.
            if (!_scopeGate.TryDrainForFloorAdvance(passBudget, token))
            {
                if (_logger.IsWarn) _logger.Warn(
                    "History window pruner published a new floor but historical read scopes opened before it did not drain within the budget; deletes for this floor are deferred to the next pass.");
                return false;
            }
        }

        // Each column gets its own budget instance so a slow account column can never starve storage/clears/markers
        // of all progress: without this, a resumed pass would always restart from "has account finished yet?" and
        // the other three columns could go passes on end without a single row examined.
        bool hasScopes = _availability.GetScopesArray().Length > 0;

        // Clears and block markers are not (yet) resolved per-key like AccountHistory/StorageHistory above - a
        // clear-context row or a block's root marker is retained down to the DEEPEST configured scope floor
        // instead, so a sliced address's clear-probe and canonicity check both stay answerable even though this
        // is coarser than strictly necessary for every other key. See HistoryReader's clear-probe remarks for why
        // collapsing clears context away would be a wrong-answer hazard, not just a missing-data one.
        ulong markersAndClearsFloor = hasScopes ? ComputeMinScopeFloor(floor) : floor;

        bool completedAccount = PruneVersionedColumn(_accountHistory, AccountCursorKey, HistoryKeyLayout.Account, floor, hasScopes, newBudget(), token);
        bool completedStorage = PruneVersionedColumn(_storageHistory, StorageCursorKey, HistoryKeyLayout.Storage, floor, hasScopes, newBudget(), token);
        bool completedClears = PruneClearsColumn(markersAndClearsFloor, newBudget(), token);
        bool completedBlocks = PruneBlockMarkers(markersAndClearsFloor, newBudget(), token);

        return completedAccount && completedStorage && completedClears && completedBlocks;
    }

    private ulong ComputeMinScopeFloor(ulong generalFloor)
    {
        ulong minFloor = generalFloor;
        ScopeFloor[] scopes = _availability.GetScopesArray();
        for (int i = 0; i < scopes.Length; i++)
        {
            if (scopes[i].Floor < minFloor) minFloor = scopes[i].Floor;
        }

        return minFloor;
    }

    /// <summary>
    /// The changesets sidecar (<c>[block BE | chunkIndex BE]</c>, block-major): retention and lifecycle are
    /// independent of the read-path window's floor - it serves devp2p replay and backfill import, not as-of-block
    /// reads, and no reader ever pins against it through <see cref="HistoryScopeGate"/>, so pruning it needs
    /// neither a published floor nor a drain. Unlike the read-path columns, block being the key's prefix makes
    /// "everything below a cutoff" a cheap contiguous-range scan with no per-key retention logic at all.
    /// </summary>
    private bool PruneChangesetSidecarColumn(Func<IPruneBudget> newBudget, CancellationToken token)
    {
        if (!_config.HistoryChangesetSidecarEnabled) return true;

        ulong sidecarRetention = _config.HistoryChangesetSidecarRetentionBlocks > 0
            ? _config.HistoryChangesetSidecarRetentionBlocks
            : _config.HistoryRetentionBlocks;

        bool completedRetention = true;
        if (sidecarRetention > 0)
        {
            ulong watermark = _writer.LastCapturedBlock;
            ulong retentionFloor = watermark > sidecarRetention ? watermark - sidecarRetention : 0;
            completedRetention = PruneSidecarBelow(retentionFloor, SidecarRetentionCursorKey, newBudget(), token);
        }

        if (!completedRetention) return false;

        long maxBytes = _config.HistoryChangesetSidecarMaxBytes;
        if (maxBytes <= 0) return true;

        if (_changesetSidecar.GatherMetric().Size <= maxBytes) return true;

        // Retention alone did not bring the column under its hard cap - drop the oldest still-retained ranges,
        // ahead of their normal retention floor, until back under budget. FlatHistorySidecarOverCapPurgedRows
        // makes this degraded state observable: it means the sidecar's retention window is genuinely too wide for
        // the configured cap, not a one-off blip.
        return PruneSidecarOverCap(maxBytes, newBudget(), token);
    }

    private bool PruneSidecarBelow(ulong floor, ReadOnlySpan<byte> cursorKeyName, IPruneBudget budget, CancellationToken token)
    {
        if (floor == 0) return true;

        ISortedKeyValueStore sorted = (ISortedKeyValueStore)_changesetSidecar;
        byte[]? cursor = ReadCursor(cursorKeyName);

        Span<byte> upperBound = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(upperBound, floor);

        int sinceFlush = 0;
        using ISortedView view = sorted.GetViewBetween(cursor ?? ReadOnlySpan<byte>.Empty, upperBound);
        IWriteBatch batch = _changesetSidecar.StartWriteBatch();
        try
        {
            while (view.MoveNext())
            {
                if (budget.Exhausted || token.IsCancellationRequested)
                {
                    WriteCursor(cursorKeyName, view.CurrentKey);
                    return false;
                }

                batch.Remove(view.CurrentKey);
                Metrics.FlatHistoryPrunedRows++;
                sinceFlush = FlushBatchIfNeeded(_changesetSidecar, ref batch, sinceFlush);
            }
        }
        finally
        {
            batch.Dispose();
        }

        ClearCursor(cursorKeyName);
        return true;
    }

    /// <summary>Deletes oldest-first from wherever the last over-cap purge (or the start of the column) left off,
    /// re-checking the actual on-disk size every <see cref="SidecarOverCapMetricCheckInterval"/> rows rather than
    /// computing a target block up front — per-row byte sizes are not tracked, so the only reliable signal is the
    /// column's own metric after a batch of deletes has actually committed.</summary>
    private bool PruneSidecarOverCap(long maxBytes, IPruneBudget budget, CancellationToken token)
    {
        ISortedKeyValueStore sorted = (ISortedKeyValueStore)_changesetSidecar;
        byte[]? cursor = ReadCursor(SidecarOverCapCursorKey);

        Span<byte> upperBound = stackalloc byte[BlockBytes + 1];
        upperBound.Fill(0xFF);

        int sinceFlush = 0;
        int sinceMetricCheck = 0;
        using ISortedView view = sorted.GetViewBetween(cursor ?? ReadOnlySpan<byte>.Empty, upperBound);
        IWriteBatch batch = _changesetSidecar.StartWriteBatch();
        try
        {
            while (view.MoveNext())
            {
                if (budget.Exhausted || token.IsCancellationRequested)
                {
                    WriteCursor(SidecarOverCapCursorKey, view.CurrentKey);
                    return false;
                }

                batch.Remove(view.CurrentKey);
                Metrics.FlatHistoryPrunedRows++;
                Metrics.FlatHistorySidecarOverCapPurgedRows++;
                sinceFlush = FlushBatchIfNeeded(_changesetSidecar, ref batch, sinceFlush);

                if (++sinceMetricCheck < SidecarOverCapMetricCheckInterval) continue;
                sinceMetricCheck = 0;

                // Force the pending deletes visible to GatherMetric before trusting its answer.
                batch.Dispose();
                batch = _changesetSidecar.StartWriteBatch();
                if (_changesetSidecar.GatherMetric().Size <= maxBytes)
                {
                    WriteCursor(SidecarOverCapCursorKey, view.CurrentKey);
                    return true;
                }
            }
        }
        finally
        {
            batch.Dispose();
        }

        // Ran out of rows entirely without getting under budget - nothing left to purge; clear the cursor so the
        // next pass starts fresh once capture has written new rows for it to reconsider.
        ClearCursor(SidecarOverCapCursorKey);
        return true;
    }

    /// <summary>
    /// <c>AccountHistory</c>/<c>StorageHistory</c>: each key's versions sit contiguously, in the suffix order
    /// <see cref="_rowFormat"/> defines. v2 (descending, post-value) iterates a group newest-first, so the first
    /// row at or below the floor is the answer every read in <c>[floor, next-change)</c> resolves to via a
    /// floor-seek — every row after it in the same run (strictly older) is dead weight, but that one must survive.
    /// v3 (ascending, pre-value) has no such row to keep: a row at or below the floor can never be the answer to a
    /// forward-seek at or above the floor (which only ever returns a row strictly above the query), so every row
    /// at or below the floor is unconditionally dead. <see cref="HistoryRowFormat.RetainsNewestRowAtOrBelowFloor"/>
    /// selects between the two so this decode/retention logic never has to assume one format over the other.
    /// </summary>
    private bool PruneVersionedColumn(IDb column, ReadOnlySpan<byte> cursorKeyName, HistoryKeyLayout keyLayout, ulong floor, bool hasScopes, IPruneBudget budget, CancellationToken token)
    {
        int flatKeyLength = keyLayout.FlatKeyLength;
        ISortedKeyValueStore sorted = (ISortedKeyValueStore)column;
        byte[]? cursor = ReadCursor(cursorKeyName);

        Span<byte> upperBound = stackalloc byte[flatKeyLength + BlockBytes + 1];
        upperBound.Fill(0xFF);

        byte[]? currentGroupKey = null;
        bool currentGroupHasFloorRow = false;
        ulong currentGroupFloor = floor;
        Span<byte> addressKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
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

                    // Byte-for-byte today's cost when no slices are configured: currentGroupFloor never leaves
                    // the pass-level floor, and neither ExtractAddressKey nor ResolveScope (a DB-free lookup, but
                    // still O(scopes) work) is ever called.
                    if (hasScopes)
                    {
                        keyLayout.ExtractAddressKey(keyPrefix, addressKey);
                        currentGroupFloor = _availability.ResolveScope(addressKey, floor).Floor;
                    }
                }

                ulong block = _rowFormat.DecodeSuffixBlock(key[flatKeyLength..]);
                if (block <= currentGroupFloor)
                {
                    if (_rowFormat.RetainsNewestRowAtOrBelowFloor && !currentGroupHasFloorRow)
                    {
                        currentGroupHasFloorRow = true;
                    }
                    else
                    {
                        batch.Remove(key);
                        Metrics.FlatHistoryPrunedRows++;
                        sinceFlush = FlushBatchIfNeeded(column, ref batch, sinceFlush);
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
        if (_config.HistoryRetentionBlocks > 0 || SidecarPruningConfigured(_config))
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
