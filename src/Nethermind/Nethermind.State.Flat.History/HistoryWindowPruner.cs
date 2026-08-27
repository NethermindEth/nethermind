// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Reclaims disk for a bounded rolling window, keeping only the rows reads in <c>[floor, watermark]</c> still need.
/// A pass is a bounded, resumable scan-and-delete per column, never a compaction, and cannot block capture: capture
/// writes above the watermark, the pruner deletes below the floor.
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
    private const double DeadWeightCompactionRatio = 0.5;

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
    private Thread? _loop;
    private ulong _lastFloorPublishWatermark;
    private bool _deletesOwed;
    private bool _accountSwept;
    private bool _storageSwept;
    private bool _clearsSwept;
    private bool _blocksSwept;
    private long _owedDrainGeneration;
    private IReadOnlyList<SliceScopeEntry>? _configuredSlices;

    /// <summary>Raised after each completed loop pass; tests synchronize on it instead of polling.</summary>
    internal Action? PassCompleted;
    private int _disposed;
    private bool _started;

    /// <summary>Called by the startup step, never the constructor, so resolving the singleton has no side
    /// effects. No-op when retention is unbounded.</summary>
    public void Start()
    {
        if (config.HistoryRetentionBlocks == 0 || _started) return;
        _started = true;
        writer.WatermarkAdvanced += OnWatermarkAdvanced;
        _wakeSignal.Release();

        // A pass is synchronous for seconds and the drain blocks, so the loop owns a thread.
        _loop = new Thread(RunLoop) { IsBackground = true, Name = "Flat history window pruner" };
        _loop.Start();
    }

    /// <summary>Removes scopes no longer configured, seeds new ones, never touches an existing floor.</summary>
    /// <exception cref="InvalidConfigurationException">Slices configured on an unwindowed database, or a slice
    /// retention shallower than the general window.</exception>
    public void ReconcileSliceScopes()
    {
        IReadOnlyList<SliceScopeEntry> configured = SliceScopeConfig.Parse(config.HistorySliceAddresses);

        if (configured.Count > 0 && !rowFormat.IsV3)
        {
            throw new InvalidConfigurationException(
                "FlatDb.HistorySliceAddresses is set, but this flatHistory database is not windowed " +
                "(HistoryRetentionBlocks is 0). Per-contract slices require the v3 pre-value format used by " +
                "windowed retention; unset HistorySliceAddresses or set HistoryRetentionBlocks.", -1);
        }

        foreach (SliceScopeEntry entry in configured)
        {
            if (entry.RetentionBlocks is { } sliceRetention && sliceRetention < config.HistoryRetentionBlocks)
            {
                throw new InvalidConfigurationException(
                    $"FlatDb.HistorySliceAddresses entry for {entry.Address} sets retention {sliceRetention}, " +
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

        // Runs for an empty list too, so removing the last slice still deletes its record.
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

    /// <summary>Advances a bounded slice's own floor toward <c>watermark - retention</c>, raise-only.</summary>
    private bool MaintainBoundedSliceFloors(ulong watermark)
    {
        _configuredSlices ??= SliceScopeConfig.Parse(config.HistorySliceAddresses);
        bool raised = false;
        foreach (SliceScopeEntry entry in _configuredSlices)
        {
            if (entry.RetentionBlocks is not { } retention || watermark <= retention) continue;
            raised |= availability.TryRaiseScopeFloor(AccountKeyOf(entry.Address), watermark - retention);
        }

        return raised;
    }

    private static byte[] AccountKeyOf(Address address) =>
        address.ToAccountPath.Bytes[..HistoryKeyLayout.ScopeKeyLength].ToArray();

    private void OnWatermarkAdvanced(ulong watermark)
    {
        // Written on the prune loop, read on the capture path, so Volatile is required.
        if (Volatile.Read(ref _disposed) != 0) return;
        if (watermark < Volatile.Read(ref _lastFloorPublishWatermark) + config.HistoryPruneIntervalBlocks) return;
        try
        {
            _wakeSignal.Release();
        }
        catch (Exception e) when (e is SemaphoreFullException or ObjectDisposedException)
        {
            if (_logger.IsTrace) _logger.Trace($"A pruner wake signal was dropped as already pending or torn down: {e.Message}");
        }
    }

    private void RunLoop()
    {
        CancellationToken token = _cts.Token;
        TimeSpan pauseAfterYield = PassBudget();
        bool yielded = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // A yielded pass owes work now, but pauses for as long as it just ran so the pruner never takes
                // more than half the wall clock from block processing. A watermark advance cuts the pause short.
                if (yielded) _wakeSignal.Wait(pauseAfterYield, token);
                else _wakeSignal.Wait(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            long passStartedAt = Stopwatch.GetTimestamp();
            try
            {
                yielded = !RunOnePass(token);
                pauseAfterYield = Stopwatch.GetElapsedTime(passStartedAt);
                PassCompleted?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("A history window pruner pass failed.", e);
                yielded = false;
            }
        }
    }

    // Never zero: a zero budget would exhaust before scanning a row, and the floor could never advance again.
    private TimeSpan PassBudget() => TimeSpan.FromSeconds(Math.Max(1, config.HistoryPrunePassBudgetSeconds));

    /// <summary>Internal so tests can drive a cycle instead of racing the wake-signal loop.</summary>
    internal bool RunOnePass(CancellationToken token, Func<IPruneBudget>? budgetFactory = null)
    {
        TimeSpan passBudget = PassBudget();
        Func<IPruneBudget> newBudget = budgetFactory ?? (() => new WallClockBudget(passBudget));

        if (RunReadPathWindowPass(passBudget, newBudget, token)) return true;

        Metrics.FlatHistoryPrunePassesYielded++;
        return false;
    }

    /// <summary>A floor advance must publish before draining old scopes and before any delete. Returns whether
    /// this pass finished all four columns.</summary>
    private bool RunReadPathWindowPass(TimeSpan passBudget, Func<IPruneBudget> newBudget, CancellationToken token)
    {
        ulong retention = config.HistoryRetentionBlocks;
        if (retention == 0) return true;

        // A drain owed by an earlier advance finishes first, on the generation that failed: bumping a fresh one
        // would demote readers already safe under it.
        if (Volatile.Read(ref _deletesOwed))
        {
            if (!scopeGate.TryDrain(Volatile.Read(ref _owedDrainGeneration), passBudget, token)) return false;
            Volatile.Write(ref _deletesOwed, false);
        }

        // The floor advances on the watermark alone. Gating it on an idle sweep instead pins the window to
        // whatever the watermark happened to be when the first floor published - on a node that starts deep,
        // that sweep outlives every later advance and the node keeps history it was configured to drop.
        ulong watermark = writer.LastCapturedBlock;
        bool scopeFloorRaised = MaintainBoundedSliceFloors(watermark);

        bool globalFloorRaised = watermark > retention && availability.TryRaiseGlobalFloor(watermark - retention);
        if (globalFloorRaised)
        {
            Metrics.FlatHistoryFloor = (long)(watermark - retention);
            Volatile.Write(ref _lastFloorPublishWatermark, watermark);
        }

        // Publish before the drain: a scope opened after this sees the new floor at its own admission check.
        if (globalFloorRaised || scopeFloorRaised)
        {
            long drainGeneration = scopeGate.BeginFloorAdvance();
            if (!scopeGate.TryDrain(drainGeneration, passBudget, token))
            {
                Volatile.Write(ref _owedDrainGeneration, drainGeneration);
                Volatile.Write(ref _deletesOwed, true);
                if (_logger.IsWarn) _logger.Warn(
                    "History window pruner published a new floor but historical read scopes opened before it did not drain within the budget; deletes for this floor are deferred to the next pass.");
                return false;
            }
        }

        // Whatever the floor is now, including one an earlier pass published: a resumed sweep deletes against the
        // current floor, and anything it passed over is taken by the next sweep.
        if (!availability.TryGetGlobalFloor(out ulong floor)) return true;

        long rowsBefore = Metrics.FlatHistoryPrunedRows;

        // Its own budget per column, so a slow account column cannot starve the other three of all progress.
        bool hasScopes = availability.GetScopesArray().Length > 0;

        // Retained down to the deepest scope floor, so a sliced address stays answerable. Coarse, never wrong.
        ulong markersAndClearsFloor = hasScopes ? ComputeMinScopeFloor(floor) : floor;

        if (!_accountSwept) _accountSwept = PruneVersionedColumn(_accountHistory, AccountCursorKey, HistoryKeyLayout.Account, floor, hasScopes, newBudget(), token);
        if (!_storageSwept) _storageSwept = PruneVersionedColumn(_storageHistory, StorageCursorKey, HistoryKeyLayout.Storage, floor, hasScopes, newBudget(), token);
        if (!_clearsSwept) _clearsSwept = PruneClearsColumn(markersAndClearsFloor, newBudget(), token);
        if (!_blocksSwept) _blocksSwept = PruneBlockMarkers(markersAndClearsFloor, newBudget(), token);

        bool completed = _accountSwept && _storageSwept && _clearsSwept && _blocksSwept;

        if (_logger.IsInfo)
        {
            long deleted = Metrics.FlatHistoryPrunedRows - rowsBefore;
            string scopeNote = markersAndClearsFloor == floor ? "" : $" Clears and markers kept down to #{markersAndClearsFloor} for slice scopes.";
            _logger.Info(completed
                ? $"Flat history sweep cycle finished, each column swept once at or below #{floor}, retaining {retention} blocks; {deleted} rows deleted this pass.{scopeNote}"
                : $"Flat history pruning below #{floor}, retaining {retention} blocks; {deleted} rows deleted this pass, "
                  + $"accounts {SweepProgress(_accountSwept, AccountCursorKey)}, storage {SweepProgress(_storageSwept, StorageCursorKey)}, "
                  + $"clears {(_clearsSwept ? "done" : "running")}, markers {(_blocksSwept ? "done" : "running")}.{scopeNote}");
        }

        if (completed)
        {
            _accountSwept = _storageSwept = _clearsSwept = _blocksSwept = false;

            bool accountCompacted = _accountHistory.CompactIfDeadWeightExceeds(DeadWeightCompactionRatio);
            bool storageCompacted = _storageHistory.CompactIfDeadWeightExceeds(DeadWeightCompactionRatio);
            if ((accountCompacted || storageCompacted) && _logger.IsInfo)
            {
                _logger.Info("Compacted the flat history columns whose files were mostly tombstones; their space has been returned.");
            }
        }

        return completed;
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

    /// <summary>Under v3 every row at or below the floor is dead, since a forward-seek only returns rows strictly
    /// above the query - and the pruner only ever runs windowed, which forces v3.</summary>
    private bool PruneVersionedColumn(IDb column, ReadOnlySpan<byte> cursorKeyName, HistoryKeyLayout keyLayout, ulong floor, bool hasScopes, IPruneBudget budget, CancellationToken token)
    {
        int flatKeyLength = keyLayout.FlatKeyLength;
        ISortedKeyValueStore sorted = (ISortedKeyValueStore)column;
        byte[]? cursor = ReadCursor(cursorKeyName);

        Span<byte> upperBound = stackalloc byte[flatKeyLength + BlockBytes + 1];
        upperBound.Fill(0xFF);

        Span<byte> currentGroupKey = stackalloc byte[flatKeyLength];
        bool hasGroup = false;
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
                    // Deletes before the cursor: a crash between them would strand the skipped rows forever.
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

                    // With no slices configured neither ExtractAddressKey nor ResolveScope is ever called.
                    if (hasScopes)
                    {
                        keyLayout.ExtractAddressKey(keyPrefix, addressKey);
                        currentGroupFloor = availability.ResolveScope(addressKey, floor).Floor;
                    }
                }

                ulong block = rowFormat.DecodeSuffixBlock(key[flatKeyLength..]);
                if (block <= currentGroupFloor)
                {
                    batch.Remove(key);
                    Metrics.FlatHistoryPrunedRows++;
                    sinceFlush = FlushBatchIfNeeded(column, ref batch, sinceFlush);
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

    /// <summary>Per account, keeps every clear at or above the floor plus the newest one below it, which a floor
    /// read still needs to tell a dead slot from a live one.</summary>
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

    /// <summary>Any marker strictly below the floor is dead: a capture connect point never verifies below it.</summary>
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

    /// <summary>Both versioned columns are keyed by a hash, so the cursor's leading bytes are a uniform position in
    /// the keyspace and give the sweep an honest percentage rather than a row count with no denominator.</summary>
    private string SweepProgress(bool completed, ReadOnlySpan<byte> cursorKeyName)
    {
        if (completed) return "done";
        byte[]? cursor = ReadCursor(cursorKeyName);
        if (cursor is null || cursor.Length < sizeof(uint)) return "0%";
        return $"{BinaryPrimitives.ReadUInt32BigEndian(cursor) * 100L / uint.MaxValue}%";
    }

    private byte[]? ReadCursor(ReadOnlySpan<byte> cursorKeyName) => _availableBlocks.Get(cursorKeyName);

    private void WriteCursor(ReadOnlySpan<byte> cursorKeyName, ReadOnlySpan<byte> key) => _availableBlocks.PutSpan(cursorKeyName, key);

    private void ClearCursor(ReadOnlySpan<byte> cursorKeyName) => _availableBlocks.Remove(cursorKeyName);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _accountHistory.InterruptCompactions();
        if (_started)
        {
            writer.WatermarkAdvanced -= OnWatermarkAdvanced;
        }

        _cts.Cancel();
        _loop?.Join();

        _cts.Dispose();
        _wakeSignal.Dispose();
    }

    private sealed class WallClockBudget(TimeSpan budget) : IPruneBudget
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public bool Exhausted => _stopwatch.Elapsed >= budget;
    }
}
