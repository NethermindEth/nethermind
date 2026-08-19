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
/// floor, a disjoint range by construction).
/// </summary>
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
    private readonly HistoryRowFormat _rowFormat;
    private readonly IFlatDbConfig _config;
    private readonly HistoryScopeGate _scopeGate;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private ulong _lastFloorPublishWatermark;

    public HistoryWindowPruner(
        HistoryWriter writer,
        IColumnsDb<FlatHistoryColumns> history,
        IFlatDbConfig config,
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
        _availability = availability;
        _rowFormat = rowFormat;
        _config = config;
        _scopeGate = scopeGate;
        _logger = logManager.GetClassLogger<HistoryWindowPruner>();

        bool shouldRun = config.HistoryRetentionBlocks > 0;
        _loop = shouldRun ? RunLoopAsync() : Task.CompletedTask;
        if (shouldRun)
        {
            writer.WatermarkAdvanced += OnWatermarkAdvanced;
        }
    }

    /// <summary>
    /// One-time (startup) reconciliation of the operator's <c>Flat.HistorySliceAddresses</c> allow-list against
    /// the scope records already on disk: deletes a scope for an address no longer configured (its rows below the
    /// general floor become prunable again on the next pass) and creates one for a newly configured address that
    /// has none yet. Never touches an already-existing scope's floor - that is the per-pass maintenance below
    /// (bounded retention), never a blind reset back to some seed value on every restart.
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
        address.ToAccountPath.Bytes[..HistoryKeyLayout.ScopeKeyLength].ToArray();

    private void OnWatermarkAdvanced(ulong watermark)
    {
        // Written from RunOnePass on the prune loop's own task, read here from whatever thread the
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
        // Never zero: a zero-duration wall-clock budget would exhaust before scanning a single row, forever
        // (HasAnyPendingCursor then stays true, so the floor can never advance again either).
        TimeSpan passBudget = TimeSpan.FromSeconds(Math.Max(1, _config.HistoryPrunePassBudgetSeconds));
        Func<IPruneBudget> newBudget = budgetFactory ?? (() => new WallClockBudget(passBudget));

        bool completedReadPathWindow = RunReadPathWindowPass(passBudget, newBudget, token);

        if (!completedReadPathWindow)
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
        Span<byte> addressKey = stackalloc byte[HistoryKeyLayout.ScopeKeyLength];
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
        const int accountKeyLength = HistoryKeyLayout.AccountKeyLength;
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
