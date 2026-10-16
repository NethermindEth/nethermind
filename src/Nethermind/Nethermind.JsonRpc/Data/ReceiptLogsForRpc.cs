// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Data;

/// <summary>A receipt's logs as <see cref="LogEntryForRpc"/> entries, built from the stored <see cref="LogEntry"/> values on demand.</summary>
/// <remarks>
/// <see cref="LogsForRpcConverter"/> writes the entries one at a time through a single reused <see cref="LogEntryForRpc"/>,
/// so serializing a receipt does not allocate an entry per log. The indexer and the enumerator build a new entry on each access.
/// The receipt's coordinates are taken when the view is created or reset.
/// </remarks>
internal sealed class ReceiptLogsForRpc : IReadOnlyList<LogEntryForRpc>
{
    private LogEntry[] _logs;
    private readonly int _offset;
    private int _logIndexStart;
    private long _transactionIndex;
    private Hash256? _transactionHash;
    private Hash256? _blockHash;
    private ulong _blockNumber;
    private ulong _blockTimestamp;

    // Write is synchronous and never nested, so one entry per thread serves every view.
    [ThreadStatic] private static LogEntryForRpc? _writeEntry;

    public ReceiptLogsForRpc(TxReceipt receipt, ulong blockTimestamp, int logIndexStart) => Reset(receipt, blockTimestamp, logIndexStart);

    /// <summary>Points the view at <paramref name="receipt"/>.</summary>
    public void Reset(TxReceipt receipt, ulong blockTimestamp, int logIndexStart)
    {
        _logs = receipt.Logs ?? [];
        Count = _logs.Length;
        _logIndexStart = logIndexStart;
        _transactionIndex = receipt.Index;
        _transactionHash = receipt.TxHash;
        _blockHash = receipt.BlockHash;
        _blockNumber = receipt.BlockNumber;
        _blockTimestamp = blockTimestamp;
    }

    private ReceiptLogsForRpc(ReceiptLogsForRpc source, int offset, int count)
    {
        _logs = source._logs;
        _offset = offset;
        Count = count;
        _logIndexStart = source._logIndexStart;
        _transactionIndex = source._transactionIndex;
        _transactionHash = source._transactionHash;
        _blockHash = source._blockHash;
        _blockNumber = source._blockNumber;
        _blockTimestamp = source._blockTimestamp;
    }

    public int Count { get; private set; }

    public LogEntryForRpc this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            LogEntryForRpc entry = CreateEntry();
            SetLog(entry, _offset + index);
            return entry;
        }
    }

    /// <summary>The run of <paramref name="count"/> logs starting at <paramref name="start"/>, keeping their receipt-wide log indexes.</summary>
    public ReceiptLogsForRpc Slice(int start, int count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)start, (uint)Count, nameof(start));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)count, (uint)(Count - start), nameof(count));
        return new(this, _offset + start, count);
    }

    public void Write(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        JsonTypeInfo<LogEntryForRpc> typeInfo = (JsonTypeInfo<LogEntryForRpc>)options.GetTypeInfo(typeof(LogEntryForRpc));
        LogEntryForRpc entry = SetReceipt(_writeEntry ??= new LogEntryForRpc());
        writer.WriteStartArray();
        for (int i = _offset; i < _offset + Count; i++)
        {
            SetLog(entry, i);
            JsonSerializer.Serialize(writer, entry, typeInfo);
        }

        // The entry outlives the view, so it must not keep this receipt's data reachable.
        entry.TransactionHash = null;
        entry.BlockHash = null;
        entry.Address = null;
        entry.Data = null;
        entry.Topics = null;
        writer.WriteEndArray();
    }

    public IEnumerator<LogEntryForRpc> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private LogEntryForRpc CreateEntry() => SetReceipt(new LogEntryForRpc());

    private LogEntryForRpc SetReceipt(LogEntryForRpc entry)
    {
        entry.Removed = false;
        entry.TransactionIndex = _transactionIndex;
        entry.TransactionHash = _transactionHash;
        entry.BlockHash = _blockHash;
        entry.BlockNumber = _blockNumber;
        entry.BlockTimestamp = _blockTimestamp;
        return entry;
    }

    private void SetLog(LogEntryForRpc entry, int logIndex)
    {
        LogEntry log = _logs[logIndex];
        entry.LogIndex = _logIndexStart + logIndex;
        entry.Address = log.Address;
        entry.Data = log.Data;
        entry.Topics = log.Topics;
    }
}
