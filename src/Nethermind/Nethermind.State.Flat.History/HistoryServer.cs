// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State;

namespace Nethermind.State.Flat.History;

public sealed class HistoryServer : IHistoryServer
{
    private const int BlockBytes = sizeof(ulong);
    private const int MinEntryChargeBytes = 32;
    private const int ScannedEntriesPerEmittedEntryBudget = 4;
    private static readonly HistoryServingScope[] NoScopes = [];
    private static readonly HistoryRowColumn[] VersionedRowColumns =
        [HistoryRowColumn.AccountHistory, HistoryRowColumn.StorageHistory, HistoryRowColumn.StorageClears, HistoryRowColumn.AvailableBlocks];

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly IDb _code;
    private readonly HistoryAvailability _availability;
    private readonly HistoryRowFormat _rowFormat;
    private readonly HistoryScopeGate _scopeGate;
    private readonly ChangesetSidecarStore? _changesetSidecar;
    private readonly IFlatDbConfig _config;
    private HistoryServingScope[] _servedScopes = NoScopes;

    public HistoryServer(
        IColumnsDb<FlatHistoryColumns> history,
        [KeyFilter(DbNames.Code)] IDb codeDb,
        IFlatDbConfig config,
        HistoryAvailability availability,
        HistoryRowFormat rowFormat,
        HistoryScopeGate scopeGate)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(codeDb);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(rowFormat);
        ArgumentNullException.ThrowIfNull(scopeGate);
        _history = history;
        _code = codeDb;
        _config = config;
        _availability = availability;
        _rowFormat = rowFormat;
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
            result[i + 1] = new HistoryServingScope(
                PadAccountKey(slice.Key, 0x00),
                PadAccountKey(slice.Key, 0xFF),
                slice.Floor,
                watermark);
        }

        return result;
    }

    /// <summary>A scope is keyed by <see cref="HistoryKeyLayout.ScopeKeyLength"/> bytes but advertised over
    /// 32-byte bounds, so it spans every account path sharing that prefix rather than the single path whose tail
    /// happens to be zero.</summary>
    private static ValueHash256 PadAccountKey(byte[] accountKey, byte fill)
    {
        Span<byte> padded = stackalloc byte[Hash256.Size];
        accountKey.CopyTo(padded);
        padded[accountKey.Length..].Fill(fill);
        return new ValueHash256(padded);
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

        ArrayPoolList<HistoryRowEntry> results = new(Math.Min(maxEntries, 1024));
        try
        {
            long consumed = 0;
            long scanned = 0;
            long scanBudget = Math.Max(maxEntries, 1L) * ScannedEntriesPerEmittedEntryBudget;

            byte[]? lastHandledKey = cursor;
            // Empty, not the cursor: the serializer restarts its delta chain at every response.
            byte[] previousEmittedKey = [];
            bool exhausted = true;

            using ISortedView view = sorted.GetViewBetween(from, endKey, ReadFlags.HintCacheMiss | ReadFlags.HintReadAhead);
            while (view.MoveNext())
            {
                if (cancellationToken.IsCancellationRequested || scanned >= scanBudget)
                {
                    exhausted = false;
                    break;
                }

                scanned++;
                ReadOnlySpan<byte> key = view.CurrentKey;
                if (column == HistoryRowColumn.AvailableBlocks && key.Length != BlockBytes)
                {
                    lastHandledKey = key.ToArray();
                    continue;
                }

                if (consumed >= byteLimit || results.Count >= maxEntries)
                {
                    exhausted = false;
                    break;
                }

                ReadOnlySpan<byte> value = view.CurrentValue;
                byte[] keyArray = key.ToArray();
                results.Add(new HistoryRowEntry(keyArray, value.ToArray()));

                // The wire delta-encodes each key against the previous one in the same response, so charging the
                // whole key overcharges by the shared prefix - which every row after the first in a key's version
                // run shares almost entirely. Charging the raw length would spend the response budget on bytes
                // that are never sent, costing round trips for nothing.
                consumed += (key.Length - SharedPrefixLength(previousEmittedKey, key)) + Math.Max(value.Length, MinEntryChargeBytes);
                previousEmittedKey = keyArray;
                lastHandledKey = keyArray;
            }

            if (!exhausted && ReferenceEquals(lastHandledKey, cursor))
            {
                results.Dispose();
                return Refuse();
            }

            return (results, exhausted ? null : lastHandledKey, false);
        }
        catch
        {
            results.Dispose();
            throw;
        }
    }

    private static int SharedPrefixLength(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
    {
        int bound = Math.Min(previous.Length, current.Length);
        int shared = 0;
        while (shared < bound && previous[shared] == current[shared]) shared++;
        return shared;
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
