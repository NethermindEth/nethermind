// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.SnapServer;

namespace Nethermind.State.Flat.History;

public sealed class HistoryServer : IHistoryServer
{
    private const int BlockBytes = sizeof(ulong);
    private const int MaxEntriesPerResponse = 131_072;
    private const int MaxChunksPerResponse = 4_096;
    private const int MinEntryChargeBytes = 32;
    private static readonly HistoryServingScope[] NoScopes = [];

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly HistoryAvailability _availability;
    private readonly ChangesetSidecarStore? _changesetSidecar;
    private readonly IFlatDbConfig _config;

    public HistoryServer(IColumnsDb<FlatHistoryColumns> history, IFlatDbConfig config)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(config);
        _history = history;
        _config = config;
        _availability = new HistoryAvailability(history.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
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
        CancellationToken cancellationToken)
    {
        byteLimit = Math.Clamp(byteLimit, 1, IHistoryServer.HardResponseByteLimit);
        if (!CanServe || !_availability.IsCovered(height) || _availability.IsBelowGlobalFloor(height))
            return (ArrayPoolList<HistoryRangeEntry>.Empty(), null);

        ISortedKeyValueStore accountHistory = (ISortedKeyValueStore)_history.GetColumnDb(FlatHistoryColumns.AccountHistory);
        (List<HistoryRangeEntry> entries, byte[]? nextCursor) = ScanAccountRange(accountHistory, startKey, endKey, height, cursor, byteLimit, cancellationToken);

        ArrayPoolList<HistoryRangeEntry> owned = new(entries.Count);
        for (int i = 0; i < entries.Count; i++) owned.Add(entries[i]);

        return (owned, nextCursor);
    }

    public async IAsyncEnumerable<ChangesetChunkEntry> GetChangesets(
        ulong fromBlockInclusive,
        ulong toBlockInclusive,
        long byteLimit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_changesetSidecar is null || !CanServe) yield break;
        if (!_availability.TryGetWatermark(out ulong watermark) || toBlockInclusive > watermark) yield break;
        if (_availability.IsBelowGlobalFloor(fromBlockInclusive)) yield break;

        byteLimit = Math.Clamp(byteLimit, 1, IHistoryServer.HardResponseByteLimit);
        List<(ulong Block, uint ChunkIndex, byte[] Payload)> chunks = _changesetSidecar.ScanRange(fromBlockInclusive, toBlockInclusive, byteLimit, MaxChunksPerResponse, cancellationToken);

        for (int i = 0; i < chunks.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            (ulong block, uint chunkIndex, byte[] payload) = chunks[i];
            bool isLastChunkForBlock = i == chunks.Count - 1 || chunks[i + 1].Block != block;
            yield return new ChangesetChunkEntry(block, chunkIndex, isLastChunkForBlock, payload);
            await Task.Yield();
        }
    }

    private static (List<HistoryRangeEntry> Entries, byte[]? NextCursor) ScanAccountRange(
        ISortedKeyValueStore accountHistory,
        in ValueHash256 startKey,
        in ValueHash256 endKey,
        ulong height,
        byte[]? cursor,
        long byteLimit,
        CancellationToken cancellationToken)
    {
        int keyLength = BaseFlatPersistence.AccountKeyLength;

        byte[] lowerBound;
        if (cursor is { Length: > 0 })
        {
            lowerBound = new byte[cursor.Length + BlockBytes + 1];
            cursor.CopyTo(lowerBound, 0);
            lowerBound.AsSpan(cursor.Length, BlockBytes).Fill(0xFF);
        }
        else
        {
            lowerBound = startKey.Bytes[..keyLength].ToArray();
        }

        byte[] upperBound = new byte[keyLength + BlockBytes + 1];
        endKey.Bytes[..keyLength].CopyTo(upperBound);
        upperBound.AsSpan(keyLength, BlockBytes).Fill(0xFF);

        List<HistoryRangeEntry> results = [];
        byte[]? nextCursor = null;
        byte[]? groupBeforeCurrent = cursor;
        byte[]? currentGroupKey = null;
        bool currentGroupAnswered = false;
        long consumed = 0;
        int entryCount = 0;

        using ISortedView view = accountHistory.GetViewBetween(lowerBound, upperBound);
        while (view.MoveNext())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                nextCursor = groupBeforeCurrent;
                break;
            }

            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != keyLength + BlockBytes) continue;

            ReadOnlySpan<byte> keyPrefix = key[..keyLength];
            if (currentGroupKey is null || !keyPrefix.SequenceEqual(currentGroupKey))
            {
                if (consumed >= byteLimit || entryCount >= MaxEntriesPerResponse)
                {
                    nextCursor = currentGroupKey ?? groupBeforeCurrent;
                    break;
                }

                groupBeforeCurrent = currentGroupKey ?? groupBeforeCurrent;
                currentGroupKey = keyPrefix.ToArray();
                currentGroupAnswered = false;
            }

            if (currentGroupAnswered) continue;

            ulong block = ~BinaryPrimitives.ReadUInt64BigEndian(key[keyLength..]);
            if (block > height) continue;

            currentGroupAnswered = true;
            byte[] value = view.CurrentValue.ToArray();
            results.Add(new HistoryRangeEntry(currentGroupKey, block, value));
            consumed += Math.Max(value.Length, MinEntryChargeBytes);
            entryCount++;
        }

        return (results, nextCursor);
    }
}
