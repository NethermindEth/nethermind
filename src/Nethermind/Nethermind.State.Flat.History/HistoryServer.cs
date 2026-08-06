// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly HistoryAvailability _availability;
    private readonly HistoryRowFormat _rowFormat;
    private readonly ChangesetSidecarStore? _changesetSidecar;
    private readonly IFlatDbConfig _config;

    public HistoryServer(IColumnsDb<FlatHistoryColumns> history, IFlatDbConfig config, HistoryAvailability availability, HistoryRowFormat rowFormat)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(rowFormat);
        _history = history;
        _config = config;
        _availability = availability;
        _rowFormat = rowFormat;
        _changesetSidecar = config.HistoryChangesetSidecarEnabled
            ? new ChangesetSidecarStore(history.GetColumnDb(FlatHistoryColumns.ChangesetSidecar))
            : null;
    }

    public bool CanServe => _config.HistoryEnabled;

    public IReadOnlyList<HistoryServingScope> ServedScopes
    {
        get
        {
            if (!CanServe || !_availability.TryGetWatermark(out ulong watermark)) return NoScopes;

            _availability.TryGetGlobalFloor(out ulong floor);
            return [new HistoryServingScope(ValueKeccak.Zero, ValueKeccak.MaxValue, floor, watermark)];
        }
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

        ISortedKeyValueStore accountHistory = (ISortedKeyValueStore)_history.GetColumnDb(FlatHistoryColumns.AccountHistory);
        (List<HistoryRangeEntry> entries, byte[]? nextCursor) = ScanAccountRange(accountHistory, _rowFormat, startKey, endKey, height, cursor, byteLimit, maxEntries, cancellationToken);

        ArrayPoolList<HistoryRangeEntry> owned = new(entries.Count);
        for (int i = 0; i < entries.Count; i++) owned.Add(entries[i]);

        return (owned, nextCursor);
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

    private static (List<HistoryRangeEntry> Entries, byte[]? NextCursor) ScanAccountRange(
        ISortedKeyValueStore accountHistory,
        HistoryRowFormat rowFormat,
        in ValueHash256 startKey,
        in ValueHash256 endKey,
        ulong height,
        byte[]? cursor,
        long byteLimit,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        int keyLength = BaseFlatPersistence.AccountKeyLength;

        byte[] searchFrom = cursor is { Length: > 0 } ? PastGroupBound(cursor, keyLength) : startKey.Bytes[..keyLength].ToArray();

        byte[] upperBound = new byte[keyLength + BlockBytes + 1];
        endKey.Bytes[..keyLength].CopyTo(upperBound);
        upperBound.AsSpan(keyLength, BlockBytes).Fill(0xFF);

        List<HistoryRangeEntry> results = [];
        byte[]? nextCursor = null;
        byte[]? lastGroupKey = cursor;
        long consumed = 0;
        int entryCount = 0;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested || consumed >= byteLimit || entryCount >= maxEntries)
            {
                nextCursor = lastGroupKey;
                break;
            }

            byte[]? groupKey = null;
            bool cancelledMidGroup = false;

            using ISortedView view = accountHistory.GetViewBetween(searchFrom, upperBound);
            while (view.MoveNext())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelledMidGroup = true;
                    break;
                }

                ReadOnlySpan<byte> key = view.CurrentKey;
                if (key.Length != keyLength + BlockBytes) continue;

                if (groupKey is null)
                {
                    groupKey = key[..keyLength].ToArray();
                }
                else if (!key[..keyLength].SequenceEqual(groupKey))
                {
                    break;
                }

                ulong block = rowFormat.DecodeSuffixBlock(key[keyLength..]);
                bool matches = rowFormat.IsV3 ? block > height : block <= height;
                if (!matches) continue;

                byte[] value = view.CurrentValue.ToArray();
                results.Add(new HistoryRangeEntry(groupKey, block, value));
                consumed += Math.Max(value.Length, MinEntryChargeBytes);
                entryCount++;
                break;
            }

            if (cancelledMidGroup)
            {
                nextCursor = lastGroupKey;
                break;
            }

            if (groupKey is null)
            {
                nextCursor = null;
                break;
            }

            lastGroupKey = groupKey;
            searchFrom = PastGroupBound(groupKey, keyLength);
        }

        return (results, nextCursor);
    }

    private static byte[] PastGroupBound(byte[] groupKeyPrefix, int keyLength)
    {
        byte[] bound = new byte[keyLength + BlockBytes + 1];
        groupKeyPrefix.AsSpan(0, keyLength).CopyTo(bound);
        bound.AsSpan(keyLength, BlockBytes).Fill(0xFF);
        return bound;
    }
}
