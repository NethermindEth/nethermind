// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

public sealed class HistoryServer : IHistoryServer
{
    private const int BlockBytes = sizeof(ulong);
    private const int MinEntryChargeBytes = 32;
    private static readonly HistoryServingScope[] NoScopes = [];
    private static readonly HistoryRowColumn[] VersionedRowColumns =
        [HistoryRowColumn.AccountHistory, HistoryRowColumn.StorageHistory, HistoryRowColumn.StorageClears, HistoryRowColumn.AvailableBlocks];

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly IDb _persistedAccounts;
    private readonly IDb _code;
    private readonly HistoryAvailability _availability;
    private readonly HistoryRowFormat _rowFormat;
    private readonly IStateHistoryCaptureStatus _captureStatus;
    private readonly HistoryScopeGate _scopeGate;
    private readonly ChangesetSidecarStore? _changesetSidecar;
    private readonly IFlatDbConfig _config;
    private HistoryServingScope[] _servedScopes = NoScopes;

    public HistoryServer(
        IColumnsDb<FlatDbColumns> db,
        IColumnsDb<FlatHistoryColumns> history,
        [KeyFilter(DbNames.Code)] IDb codeDb,
        IFlatDbConfig config,
        HistoryAvailability availability,
        HistoryRowFormat rowFormat,
        IStateHistoryCaptureStatus captureStatus,
        HistoryScopeGate scopeGate)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(codeDb);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(rowFormat);
        ArgumentNullException.ThrowIfNull(captureStatus);
        ArgumentNullException.ThrowIfNull(scopeGate);
        _history = history;
        _persistedAccounts = db.GetColumnDb(FlatDbColumns.Account);
        _code = codeDb;
        _config = config;
        _availability = availability;
        _rowFormat = rowFormat;
        _captureStatus = captureStatus;
        _scopeGate = scopeGate;
        _changesetSidecar = config.HistoryChangesetSidecarEnabled
            ? new ChangesetSidecarStore(history.GetColumnDb(FlatHistoryColumns.ChangesetSidecar))
            : null;

        _availability.Changed += RefreshServedScopes;
        RefreshServedScopes();
    }

    public bool CanServe => _config.HistoryEnabled;

    private bool IsWindowed => _config.HistoryRetentionBlocks > 0 || _availability.TryGetGlobalFloor(out _);

    public bool CanServeFullClone => CanServe && !IsWindowed;

    public byte RowFormatVersion => _rowFormat.FormatVersion;

    public IReadOnlyList<HistoryServingScope> ServedScopes => Volatile.Read(ref _servedScopes);

    private void RefreshServedScopes() => Volatile.Write(ref _servedScopes, ComputeServedScopes());

    private HistoryServingScope[] ComputeServedScopes()
    {
        if (!CanServe || !_availability.TryGetWatermark(out ulong watermark)) return NoScopes;

        _availability.TryGetGlobalFloor(out ulong floor);
        IReadOnlyList<ScopeFloor> slices = _availability.GetScopes();
        if (slices.Count == 0) return [new HistoryServingScope(ValueKeccak.Zero, ValueKeccak.MaxValue, floor, watermark)];

        HistoryServingScope[] result = new HistoryServingScope[slices.Count + 1];
        result[0] = new HistoryServingScope(ValueKeccak.Zero, ValueKeccak.MaxValue, floor, watermark);
        for (int i = 0; i < slices.Count; i++)
        {
            ScopeFloor slice = slices[i];
            ValueHash256 sliceStart = PadAccountKey(slice.Key);
            result[i + 1] = new HistoryServingScope(sliceStart, sliceStart, slice.Floor, watermark);
        }

        return result;
    }

    private static ValueHash256 PadAccountKey(byte[] accountKey)
    {
        Span<byte> padded = stackalloc byte[32];
        accountKey.CopyTo(padded);
        return new ValueHash256(padded);
    }

    public (IOwnedReadOnlyList<HistoryRangeEntry> Entries, byte[]? NextCursor) GetHistoryRangeAtHeight(
        in ValueHash256 startKey,
        in ValueHash256 endKey,
        ulong height,
        byte[]? cursor,
        long byteLimit,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        byteLimit = Math.Clamp(byteLimit, 1, IHistoryServer.HardResponseByteLimit);
        if (!CanServe || !_availability.IsCovered(height) || _availability.IsBelowGlobalFloor(height))
            return (ArrayPoolList<HistoryRangeEntry>.Empty(), null);

        if (cursor is not null && cursor.Length != BaseFlatPersistence.AccountKeyLength)
            return (ArrayPoolList<HistoryRangeEntry>.Empty(), null);

        int scopeEpoch = _scopeGate.EnterScope();
        try
        {
            // HistoricalFlatDbManager's own re-check pattern: the pre-check above is only a routing decision, not
            // atomic with scope registration - a floor-advance's publish-then-drain-then-delete could complete
            // entirely in the gap between it and EnterScope. Re-checking after entering closes that window.
            if (!_availability.IsCovered(height) || _availability.IsBelowGlobalFloor(height))
                return (ArrayPoolList<HistoryRangeEntry>.Empty(), null);

            (List<HistoryRangeEntry> entries, byte[]? nextCursor) = ScanWithSkewRetry(startKey, endKey, height, cursor, byteLimit, maxEntries, cancellationToken);

            ArrayPoolList<HistoryRangeEntry> owned = new(entries.Count);
            for (int i = 0; i < entries.Count; i++) owned.Add(entries[i]);

            return (owned, nextCursor);
        }
        finally
        {
            _scopeGate.ExitScope(scopeEpoch);
        }
    }

    /// <summary>
    /// The persisted flat column can advance (a persist round completing) between when the history side of the
    /// merge below observes the watermark and when the flat side reads it - the cheap fail-closed answer is not a
    /// pinned cross-column snapshot (not available here) but a watermark check before and after: if it moved, the
    /// two sides may be mutually inconsistent, so the whole page is retried once. A second skew still returns the
    /// (still internally consistent, just possibly stale) result with its resume cursor rather than looping.
    /// </summary>
    private (List<HistoryRangeEntry> Entries, byte[]? NextCursor) ScanWithSkewRetry(
        in ValueHash256 startKey, in ValueHash256 endKey, ulong height, byte[]? cursor, long byteLimit, int maxEntries, CancellationToken cancellationToken)
    {
        _availability.TryGetWatermark(out ulong watermarkBefore);
        (List<HistoryRangeEntry> entries, byte[]? nextCursor) = ScanOnce(startKey, endKey, height, cursor, byteLimit, maxEntries, cancellationToken);
        _availability.TryGetWatermark(out ulong watermarkAfter);
        if (watermarkBefore == watermarkAfter) return (entries, nextCursor);

        return ScanOnce(startKey, endKey, height, cursor, byteLimit, maxEntries, cancellationToken);
    }

    /// <summary>
    /// A single sorted union-merge walk over the history column (changed keys) and, for v3 with healthy capture,
    /// the live persisted flat column (unchanged-since-height keys) — the flat side is never a superset substitute
    /// for the history side: a key destroyed before the tip still exists in history (with the pre-destruct value)
    /// but not in the live flat column, and at the requested height it existed and must still be emitted. One
    /// shared byte/entry budget and one cursor (the last key the unified stream emitted) cover both sides; on
    /// resume each side re-seeks past that same key in its own domain (<see cref="GroupUpperBound"/> for history's
    /// block-suffixed groups, <see cref="PastFlatKeyBound"/> for the flat column's unsuffixed keys).
    /// </summary>
    private (List<HistoryRangeEntry> Entries, byte[]? NextCursor) ScanOnce(
        in ValueHash256 startKey, in ValueHash256 endKey, ulong height, byte[]? cursor, long byteLimit, int maxEntries, CancellationToken cancellationToken)
    {
        int keyLength = BaseFlatPersistence.AccountKeyLength;
        bool mergeFlatFallback = _rowFormat.IsV3 && _captureStatus.CaptureHealthy;

        ISortedKeyValueStore accountHistory = (ISortedKeyValueStore)_history.GetColumnDb(FlatHistoryColumns.AccountHistory);
        ReadOnlySpan<byte> endKeyPrefix = endKey.Bytes[..keyLength];

        byte[] historySearchFrom = cursor is { Length: > 0 } ? GroupUpperBound(cursor, keyLength) : startKey.Bytes[..keyLength].ToArray();
        byte[] historyUpperBound = GroupUpperBound(endKeyPrefix, keyLength);

        ISortedView? flatView = null;
        if (mergeFlatFallback)
        {
            ISortedKeyValueStore accountFlat = (ISortedKeyValueStore)_persistedAccounts;
            byte[] flatSearchFrom = cursor is { Length: > 0 } ? PastFlatKeyBound(cursor, keyLength) : startKey.Bytes[..keyLength].ToArray();
            byte[] flatUpperBound = GroupUpperBound(endKeyPrefix, keyLength);
            flatView = accountFlat.GetViewBetween(flatSearchFrom, flatUpperBound);
        }

        using ISortedView historyView = accountHistory.GetViewBetween(historySearchFrom, historyUpperBound);
        try
        {
            return MergeWalk(historyView, flatView, keyLength, height, cursor, byteLimit, maxEntries, cancellationToken);
        }
        finally
        {
            flatView?.Dispose();
        }
    }

    private (List<HistoryRangeEntry> Entries, byte[]? NextCursor) MergeWalk(
        ISortedView historyView, ISortedView? flatView, int keyLength, ulong height, byte[]? cursor, long byteLimit, int maxEntries, CancellationToken cancellationToken)
    {
        List<HistoryRangeEntry> results = [];
        long consumed = 0;
        int entryCount = 0;
        byte[]? lastEmittedKey = cursor;

        bool historyMoved = MoveToValidRow(historyView, keyLength + BlockBytes);
        bool flatMoved = flatView is not null && MoveToValidRow(flatView, keyLength);
        byte[]? flatKey = flatMoved ? flatView!.CurrentKey.ToArray() : null;
        byte[]? flatValue = flatMoved ? flatView!.CurrentValue.ToArray() : null;

        while (true)
        {
            byte[]? historyGroupKey = historyMoved ? historyView.CurrentKey[..keyLength].ToArray() : null;
            if (historyGroupKey is null && flatKey is null) break;
            if (cancellationToken.IsCancellationRequested || consumed >= byteLimit || entryCount >= maxEntries) break;

            int cmp = historyGroupKey is null ? 1 : flatKey is null ? -1 : ((ReadOnlySpan<byte>)historyGroupKey).SequenceCompareTo(flatKey);
            bool candidateIsHistory = cmp <= 0;
            byte[] candidateKey = candidateIsHistory ? historyGroupKey! : flatKey!;
            bool candidateAlsoFlat = flatKey is not null && ((ReadOnlySpan<byte>)flatKey).SequenceEqual(candidateKey);

            ulong? matchedBlock = null;
            byte[]? matchedValue = null;

            if (candidateIsHistory)
            {
                while (historyMoved && historyView.CurrentKey[..keyLength].SequenceEqual(candidateKey))
                {
                    ulong block = _rowFormat.DecodeSuffixBlock(historyView.CurrentKey[keyLength..]);
                    bool matches = _rowFormat.IsV3 ? block > height : block <= height;
                    if (matches && matchedBlock is null)
                    {
                        matchedBlock = block;
                        matchedValue = historyView.CurrentValue.ToArray();
                    }

                    historyMoved = MoveToValidRow(historyView, keyLength + BlockBytes);
                }
            }

            if (matchedBlock is not null)
            {
                results.Add(new HistoryRangeEntry(candidateKey, matchedBlock.Value, matchedValue!, IsLiveFallback: false));
                consumed += Math.Max(matchedValue!.Length, MinEntryChargeBytes);
                entryCount++;
            }
            else if (candidateAlsoFlat)
            {
                results.Add(new HistoryRangeEntry(candidateKey, height, flatValue!, IsLiveFallback: true));
                consumed += Math.Max(flatValue!.Length, MinEntryChargeBytes);
                entryCount++;
            }

            lastEmittedKey = candidateKey;

            if (candidateAlsoFlat)
            {
                flatMoved = MoveToValidRow(flatView!, keyLength);
                flatKey = flatMoved ? flatView!.CurrentKey.ToArray() : null;
                flatValue = flatMoved ? flatView!.CurrentValue.ToArray() : null;
            }
        }

        bool exhausted = !historyMoved && flatKey is null && !cancellationToken.IsCancellationRequested;
        return (results, exhausted ? null : lastEmittedKey);
    }

    private static bool MoveToValidRow(ISortedView view, int expectedKeyLength)
    {
        while (view.MoveNext())
        {
            if (view.CurrentKey.Length == expectedKeyLength) return true;
        }

        return false;
    }

    public async IAsyncEnumerable<ChangesetChunkEntry> GetChangesets(
        ulong fromBlockInclusive,
        ulong toBlockInclusive,
        long byteLimit,
        int maxChunks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_changesetSidecar is null || !CanServe) yield break;
        if (!_availability.TryGetWatermark(out ulong watermark) || toBlockInclusive > watermark) yield break;
        if (_availability.IsBelowGlobalFloor(fromBlockInclusive)) yield break;

        byteLimit = Math.Clamp(byteLimit, 1, IHistoryServer.HardResponseByteLimit);
        List<(ulong Block, uint ChunkIndex, byte[] Payload)> chunks = _changesetSidecar.ScanRange(fromBlockInclusive, toBlockInclusive, byteLimit, maxChunks, cancellationToken);

        for (int i = 0; i < chunks.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            (ulong block, uint chunkIndex, byte[] payload) = chunks[i];
            bool isLastChunkForBlock = _changesetSidecar.TryGetChunk(block, chunkIndex + 1) is null;
            yield return new ChangesetChunkEntry(block, chunkIndex, isLastChunkForBlock, payload);
            await Task.Yield();
        }
    }

    private static byte[] PastFlatKeyBound(byte[] flatKey, int keyLength)
    {
        byte[] bound = new byte[keyLength + 1];
        flatKey.AsSpan(0, Math.Min(flatKey.Length, keyLength)).CopyTo(bound);
        return bound;
    }

    private static byte[] GroupUpperBound(ReadOnlySpan<byte> keyPrefix, int keyLength)
    {
        byte[] bound = new byte[keyLength + BlockBytes + 1];
        keyPrefix[..Math.Min(keyPrefix.Length, keyLength)].CopyTo(bound);
        bound.AsSpan(keyLength, BlockBytes).Fill(0xFF);
        return bound;
    }

    public (IOwnedReadOnlyList<HistoryRowEntry> Entries, byte[]? NextCursor, bool Refused) GetHistoryRows(
        HistoryRowColumn column,
        byte[] startKey,
        byte[] endKey,
        byte[]? cursor,
        long byteLimit,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        if (!CanServe || (Array.IndexOf(VersionedRowColumns, column) >= 0 && IsWindowed)) return Refuse();

        IDb? source = ResolveColumn(column);
        if (source is not ISortedKeyValueStore sorted) return Refuse();

        byteLimit = Math.Clamp(byteLimit, 1, IHistoryServer.HardResponseByteLimit);

        int scopeEpoch = _scopeGate.EnterScope();
        try
        {
            if (Array.IndexOf(VersionedRowColumns, column) >= 0 && IsWindowed) return Refuse();

            return ScanRows(column, sorted, startKey, endKey, cursor, byteLimit, maxEntries, cancellationToken);
        }
        finally
        {
            _scopeGate.ExitScope(scopeEpoch);
        }
    }

    private (IOwnedReadOnlyList<HistoryRowEntry> Entries, byte[]? NextCursor, bool Refused) ScanRows(
        HistoryRowColumn column, ISortedKeyValueStore sorted, byte[] startKey, byte[] endKey, byte[]? cursor, long byteLimit, int maxEntries, CancellationToken cancellationToken)
    {
        byte[] from = cursor is { Length: > 0 } ? RawRowKeys.NextKeyAfter(cursor) : startKey;

        ArrayPoolList<HistoryRowEntry> results = new(16);
        try
        {
            long consumed = 0;
            byte[]? lastEmittedKey = cursor;
            bool exhausted = true;

            using ISortedView view = sorted.GetViewBetween(from, endKey, ReadFlags.HintCacheMiss);
            while (view.MoveNext())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    results.Dispose();
                    return Refuse();
                }

                ReadOnlySpan<byte> key = view.CurrentKey;
                if (column == HistoryRowColumn.AvailableBlocks && key.Length != BlockBytes) continue;

                if (consumed >= byteLimit || results.Count >= maxEntries)
                {
                    exhausted = false;
                    break;
                }

                ReadOnlySpan<byte> value = view.CurrentValue;
                byte[] keyArray = key.ToArray();
                results.Add(new HistoryRowEntry(keyArray, value.ToArray()));
                consumed += key.Length + Math.Max(value.Length, MinEntryChargeBytes);
                lastEmittedKey = keyArray;
            }

            return (results, exhausted ? null : lastEmittedKey, false);
        }
        catch
        {
            results.Dispose();
            throw;
        }
    }

    private static (IOwnedReadOnlyList<HistoryRowEntry> Entries, byte[]? NextCursor, bool Refused) Refuse() =>
        (ArrayPoolList<HistoryRowEntry>.Empty(), null, true);

    private IDb? ResolveColumn(HistoryRowColumn column) => column switch
    {
        HistoryRowColumn.AccountHistory => _history.GetColumnDb(FlatHistoryColumns.AccountHistory),
        HistoryRowColumn.StorageHistory => _history.GetColumnDb(FlatHistoryColumns.StorageHistory),
        HistoryRowColumn.StorageClears => _history.GetColumnDb(FlatHistoryColumns.StorageClears),
        HistoryRowColumn.AvailableBlocks => _history.GetColumnDb(FlatHistoryColumns.AvailableBlocks),
        HistoryRowColumn.Code => _code,
        _ => null,
    };
}
