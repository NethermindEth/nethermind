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

/// <summary>
/// Reclaims disk for a bounded rolling window: as the watermark advances, keeps only the row(s) each key's
/// row format requires for reads in <c>[floor, watermark]</c> to keep resolving, and deletes the rest. A single
/// pass is a bounded, resumable scan-and-delete over each history column — never a whole-column compaction — so
/// it never blocks capture (which only ever writes above the watermark; the pruner only ever deletes below the
/// floor, a disjoint range by construction).
/// </summary>
public sealed class HistoryWindowPruner(
    HistoryWriter writer,
    IColumnsDb<FlatHistoryColumns> history,
    IFlatDbConfig config,
    HistoryScopeGate scopeGate,
    HistoryAvailability availability,
    HistoryRowFormat rowFormat,
    ILogManager logManager) : IDisposable
{
    private const int BlockBytes = sizeof(ulong);
    private const int FlushEveryNDeletes = 1000;

    private static ReadOnlySpan<byte> AccountCursorKey => "history:prune:cursor:account"u8;
    private static ReadOnlySpan<byte> StorageCursorKey => "history:prune:cursor:storage"u8;
    private static ReadOnlySpan<byte> ClearsCursorKey => "history:prune:cursor:clears"u8;
    private static ReadOnlySpan<byte> BlocksCursorKey => "history:prune:cursor:blocks"u8;
    private readonly IDb _availableBlocks = history.GetColumnDb(FlatHistoryColumns.AvailableBlocks);
    private readonly IDb _accountHistory = history.GetColumnDb(FlatHistoryColumns.AccountHistory);
    private readonly IDb _storageHistory = history.GetColumnDb(FlatHistoryColumns.StorageHistory);
    private readonly IDb _storageClears = history.GetColumnDb(FlatHistoryColumns.StorageClears);
    private readonly ILogger _logger = logManager.GetClassLogger<HistoryWindowPruner>();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;
    private ulong _lastFloorPublishWatermark;
    private IReadOnlyList<SliceScopeEntry>? _configuredSlices;
    private bool _disposed;
    private bool _started;

    /// <summary>Subscribes to watermark advances and launches the prune loop. Called by the startup step (after
    /// the block tree is initialized), never from the constructor, so resolving the singleton has no side effects.
    /// No-op when retention is unbounded.</summary>
    public void Start()
    {
        if (config.HistoryRetentionBlocks == 0 || _started) return;
        _started = true;
        writer.WatermarkAdvanced += OnWatermarkAdvanced;
        _loop = RunLoopAsync();
    }

    /// <summary>One-time (startup) reconciliation of the allow-list against the scope records on disk: removes
    /// scopes no longer configured, seeds new ones, never touches an existing scope's floor.</summary>
    /// <exception cref="InvalidConfigurationException">Slices configured on an unwindowed database, or a slice
    /// retention shallower than the general window.</exception>
    public void ReconcileSliceScopes()
    {
        IReadOnlyList<SliceScopeEntry> configured = SliceScopeConfig.Parse(config.HistorySliceAddresses);

        if (configured.Count > 0 && !rowFormat.IsV3)
        {
            throw new InvalidConfigurationException(
                "Flat.HistorySliceAddresses is set, but this flatHistory database is not windowed " +
                "(HistoryRetentionBlocks is 0). Per-contract slices require the v3 pre-value format used by " +
                "windowed retention; unset HistorySliceAddresses or set HistoryRetentionBlocks.", -1);
        }

        foreach (SliceScopeEntry entry in configured)
        {
            if (entry.RetentionBlocks is { } sliceRetention && sliceRetention < config.HistoryRetentionBlocks)
            {
                throw new InvalidConfigurationException(
                    $"Flat.HistorySliceAddresses entry for {entry.Address} sets retention {sliceRetention}, " +
                    $"shallower than HistoryRetentionBlocks ({config.HistoryRetentionBlocks}). A slice can only " +
                    "deepen retention: a shallower one would delete that address's rows inside the general window " +
                    "while reads still resolve them from live state, silently returning wrong historical values.", -1);
            }
        }

        Dictionary<byte[], SliceScopeEntry> configuredByKey = new(configured.Count, Bytes.EqualityComparer);
        foreach (SliceScopeEntry entry in configured)
        {
            configuredByKey[AccountKeyOf(entry.Address)] = entry;
        }

        // Runs even for an empty configured list, so removing the last remaining slice from the allow-list still
        // deletes its scope record instead of leaving it orphaned on disk.
        foreach (ScopeFloor existing in availability.GetScopes())
        {
            if (!configuredByKey.ContainsKey(existing.Key))
            {
                availability.RemoveScope(existing.Key);
            }
        }

        if (configured.Count == 0) return;

        availability.TryGetGlobalFloor(out ulong currentGeneralFloor);
        ulong watermark = writer.LastCapturedBlock;

        foreach ((byte[] key, SliceScopeEntry entry) in configuredByKey)
        {
            if (availability.TryGetScopeFloor(key, out _)) continue;

            ulong seedFloor = currentGeneralFloor;
            if (entry.RetentionBlocks is { } retention)
            {
                ulong retentionFloor = watermark > retention ? watermark - retention : 0;
                seedFloor = Math.Max(currentGeneralFloor, retentionFloor);
            }

            availability.PublishScope(key, seedFloor);
        }
    }

    /// <summary>Per-pass maintenance for a slice configured with a bounded (not unbounded) retention: advances its
    /// own floor toward <c>watermark - retention</c> as the watermark grows, the same way the general floor
    /// advances - never past its own current value (<see cref="HistoryAvailability.TryRaiseScopeFloor"/> is a
    /// raise-only CAS), and never below <see cref="ReconcileSliceScopes"/>'s seed.</summary>
    private void MaintainBoundedSliceFloors(ulong watermark)
    {
        _configuredSlices ??= SliceScopeConfig.Parse(config.HistorySliceAddresses);
        foreach (SliceScopeEntry entry in _configuredSlices)
        {
            if (entry.RetentionBlocks is not { } retention || watermark <= retention) continue;
            availability.TryRaiseScopeFloor(AccountKeyOf(entry.Address), watermark - retention);
        }
    }

    private static byte[] AccountKeyOf(Address address) =>
        address.ToAccountPath.Bytes[..HistoryKeyLayout.ScopeKeyLength].ToArray();

    private void OnWatermarkAdvanced(ulong watermark)
    {
        // Written from RunOnePass on the prune loop's own task, read here from whatever thread the
        // writer's capture path runs on: Volatile is the correctness requirement, not the plain field a
        // single-writer/single-reader assumption would permit.
        if (Volatile.Read(ref _disposed)) return;
        if (watermark < Volatile.Read(ref _lastFloorPublishWatermark) + config.HistoryPruneIntervalBlocks) return;
        try { _wakeSignal.Release(); } catch (Exception e) when (e is SemaphoreFullException or ObjectDisposedException) { }
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

    /// <summary>Runs one prune cycle synchronously - internal so tests can drive it deterministically instead of
    /// racing the wake-signal loop; <paramref name="budgetFactory"/> is called once per column.</summary>
    internal void RunOnePass(CancellationToken token, Func<IPruneBudget>? budgetFactory = null)
    {
        // Never zero: a zero-duration wall-clock budget would exhaust before scanning a single row, forever
        // (HasAnyPendingCursor then stays true, so the floor can never advance again either).
        TimeSpan passBudget = TimeSpan.FromSeconds(Math.Max(1, config.HistoryPrunePassBudgetSeconds));
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
        ulong retention = config.HistoryRetentionBlocks;
        if (retention == 0) return true;

        ulong floor;
        if (HasAnyPendingCursor())
        {
            // A previous pass yielded mid-column for the already-published floor: resume that work. Computing
            // and comparing a fresh floor here would see "no advance" (the floor was already published) and skip
            // the pass entirely, silently abandoning the deletes it still owes for the current window.
            if (!availability.TryGetGlobalFloor(out floor)) return true;
        }
        else
        {
            ulong watermark = writer.LastCapturedBlock;
            MaintainBoundedSliceFloors(watermark);
            if (watermark <= retention) return true;

            ulong newFloor = watermark - retention;

            if (!availability.TryRaiseGlobalFloor(newFloor)) return true;

            Metrics.FlatHistoryFloor = (long)newFloor;
            Volatile.Write(ref _lastFloorPublishWatermark, watermark);
            floor = newFloor;

            // Floor publishes before the drain (and before any delete): a scope opened after this point already
            // sees the new floor at its own admission check, so it is safe by construction regardless of which
            // epoch it lands in — only scopes admitted under the old, lower floor need draining before deleting.
            if (!scopeGate.TryDrainForFloorAdvance(passBudget, token))
            {
                if (_logger.IsWarn) _logger.Warn(
                    "History window pruner published a new floor but historical read scopes opened before it did not drain within the budget; deletes for this floor are deferred to the next pass.");
                return false;
            }
        }

        // Each column gets its own budget instance so a slow account column can never starve storage/clears/markers
        // of all progress: without this, a resumed pass would always restart from "has account finished yet?" and
        // the other three columns could go passes on end without a single row examined.
        bool hasScopes = availability.GetScopesArray().Length > 0;

        // Clears and block markers are retained down to the DEEPEST configured scope floor, so a sliced address's
        // clear-probe and canonicity check stay answerable - coarser than per-key, but never a wrong answer.
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
        ScopeFloor[] scopes = availability.GetScopesArray();
        for (int i = 0; i < scopes.Length; i++)
        {
            if (scopes[i].Floor < minFloor) minFloor = scopes[i].Floor;
        }

        return minFloor;
    }

    /// <summary>v2 must keep each key's newest row at or below the floor (it is the answer every read in
    /// <c>[floor, next-change)</c> resolves to); under v3 every row at or below the floor is unconditionally dead
    /// (a forward-seek only ever returns rows strictly above the query).
    /// <see cref="HistoryRowFormat.RetainsNewestRowAtOrBelowFloor"/> selects between the two.</summary>
    private bool PruneVersionedColumn(IDb column, ReadOnlySpan<byte> cursorKeyName, HistoryKeyLayout keyLayout, ulong floor, bool hasScopes, IPruneBudget budget, CancellationToken token)
    {
        int flatKeyLength = keyLayout.FlatKeyLength;
        ISortedKeyValueStore sorted = (ISortedKeyValueStore)column;
        byte[]? cursor = ReadCursor(cursorKeyName);

        Span<byte> upperBound = stackalloc byte[flatKeyLength + BlockBytes + 1];
        upperBound.Fill(0xFF);

        Span<byte> currentGroupKey = stackalloc byte[flatKeyLength];
        bool hasGroup = false;
        bool currentGroupHasFloorRow = false;
        ulong currentGroupFloor = floor;
        Span<byte> addressKey = stackalloc byte[HistoryKeyLayout.ScopeKeyLength];
        int sinceFlush = 0;

        using ISortedView view = sorted.GetViewBetween(cursor ?? ReadOnlySpan<byte>.Empty, upperBound, ReadFlags.HintCacheMiss);
        IWriteBatch batch = column.StartWriteBatch();
        try
        {
            while (view.MoveNext())
            {
                if (budget.Exhausted || token.IsCancellationRequested)
                {
                    // Deletes must land before the cursor does: a crash between the two would otherwise strand
                    // the skipped-over rows below the floor forever.
                    batch.Dispose();
                    batch = column.StartWriteBatch();
                    WriteCursor(cursorKeyName, view.CurrentKey);
                    return false;
                }

                ReadOnlySpan<byte> key = view.CurrentKey;
                if (key.Length != flatKeyLength + BlockBytes) continue;

                ReadOnlySpan<byte> keyPrefix = key[..flatKeyLength];
                if (!hasGroup || !keyPrefix.SequenceEqual(currentGroupKey))
                {
                    keyPrefix.CopyTo(currentGroupKey);
                    hasGroup = true;
                    currentGroupHasFloorRow = false;

                    // Byte-for-byte today's cost when no slices are configured: currentGroupFloor never leaves
                    // the pass-level floor, and neither ExtractAddressKey nor ResolveScope (a DB-free lookup, but
                    // still O(scopes) work) is ever called.
                    if (hasScopes)
                    {
                        keyLayout.ExtractAddressKey(keyPrefix, addressKey);
                        currentGroupFloor = availability.ResolveScope(addressKey, floor).Floor;
                    }
                }

                ulong block = rowFormat.DecodeSuffixBlock(key[flatKeyLength..]);
                if (block <= currentGroupFloor)
                {
                    if (rowFormat.RetainsNewestRowAtOrBelowFloor && !currentGroupHasFloorRow)
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

        using ISortedView view = sorted.GetViewBetween(cursor ?? ReadOnlySpan<byte>.Empty, upperBound, ReadFlags.HintCacheMiss);
        IWriteBatch batch = _storageClears.StartWriteBatch();
        try
        {
            while (view.MoveNext())
            {
                if (budget.Exhausted || token.IsCancellationRequested)
                {
                    batch.Dispose();
                    batch = _storageClears.StartWriteBatch();
                    WriteCursor(ClearsCursorKey, view.CurrentKey);
                    return false;
                }

                ReadOnlySpan<byte> key = view.CurrentKey;
                if (key.Length != accountKeyLength + BlockBytes) continue;

                ulong block = BinaryPrimitives.ReadUInt64BigEndian(key[accountKeyLength..]);
                if (block < floor)
                {
                    if (pendingBelowFloorKey is not null && key[..accountKeyLength].SequenceEqual(pendingBelowFloorKey.AsSpan(0, accountKeyLength)))
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
        using ISortedView view = sorted.GetViewBetween(cursor ?? ReadOnlySpan<byte>.Empty, upperBound, ReadFlags.HintCacheMiss);
        IWriteBatch batch = _availableBlocks.StartWriteBatch();
        try
        {
            while (view.MoveNext())
            {
                if (budget.Exhausted || token.IsCancellationRequested)
                {
                    batch.Dispose();
                    batch = _availableBlocks.StartWriteBatch();
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
        Volatile.Write(ref _disposed, true);
        if (_started)
        {
            writer.WatermarkAdvanced -= OnWatermarkAdvanced;
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
