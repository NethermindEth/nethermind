// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Db.LogIndex;

/// <summary>The log addresses and topics of one block, the only parts of its receipts the log index reads.</summary>
/// <remarks>
/// Each log is stored as its address, its topic count capped at <see cref="LogIndexStorage.MaxTopics"/>, and those topics.
/// The logs live in a pooled array that <see cref="Dispose"/> returns, so neither a disposed instance nor its copies may be read.
/// </remarks>
public readonly record struct BlockReceipts : IDisposable
{
    private readonly byte[] _logs;
    private readonly int _logsLength;

    public int BlockNumber { get; }
    public int TxCount { get; }

    internal BlockReceipts(int blockNumber, int txCount, byte[] logs, int logsLength)
    {
        BlockNumber = blockNumber;
        TxCount = txCount;
        _logs = logs;
        _logsLength = logsLength;
    }

    public BlockReceipts(int blockNumber, TxReceipt[] receipts)
    {
        BlockLogsWriter writer = new();
        foreach (TxReceipt receipt in receipts)
        {
            foreach (LogEntry log in receipt.Logs ?? [])
            {
                writer.AddLog(log.Address.Bytes);
                foreach (Hash256 topic in log.Topics)
                {
                    if (!writer.TryAddTopic(topic.Bytes))
                        break;
                }
            }
        }

        this = writer.Build(blockNumber, receipts.Length);
    }

    public LogReader ReadLogs() => new(_logs.AsSpan(0, _logsLength));

    public void Dispose()
    {
        if (_logs is { Length: > 0 })
            ArrayPool<byte>.Shared.Return(_logs);
    }

    /// <summary>Reads the logs of a <see cref="BlockReceipts"/> one at a time.</summary>
    public ref struct LogReader(ReadOnlySpan<byte> logs)
    {
        private ReadOnlySpan<byte> _remaining = logs;

        /// <param name="address">Receives the log address.</param>
        /// <param name="topics">Receives the log topics, <see cref="Hash256.Size"/> bytes each.</param>
        public bool TryGetNext(out ReadOnlySpan<byte> address, out ReadOnlySpan<byte> topics)
        {
            if (_remaining.IsEmpty)
            {
                address = default;
                topics = default;
                return false;
            }

            address = _remaining[..Address.Size];
            int topicsLength = _remaining[Address.Size] * Hash256.Size;
            topics = _remaining.Slice(Address.Size + 1, topicsLength);
            _remaining = _remaining[(Address.Size + 1 + topicsLength)..];
            return true;
        }
    }
}

/// <summary>Collects the log addresses and topics of one block into a <see cref="BlockReceipts"/>.</summary>
/// <remarks>The buffer is pooled: <see cref="Build"/> hands it over, <see cref="Dispose"/> returns it.</remarks>
public ref struct BlockLogsWriter()
{
    private const int MinCapacity = 4096;

    private byte[] _buffer = [];
    private int _length;
    private int _topicCountPosition = -1;

    public void AddLog(scoped ReadOnlySpan<byte> address)
    {
        Span<byte> destination = Reserve(Address.Size + 1);
        address.CopyTo(destination);
        destination[^1] = 0;
        _topicCountPosition = _length - 1;
    }

    /// <summary>Adds a topic to the last added log.</summary>
    /// <returns><c>false</c> when the log already has <see cref="LogIndexStorage.MaxTopics"/> topics, as the index reads no more.</returns>
    public bool TryAddTopic(scoped ReadOnlySpan<byte> topic)
    {
        if (_buffer[_topicCountPosition] == LogIndexStorage.MaxTopics)
            return false;

        topic.CopyTo(Reserve(Hash256.Size));
        _buffer[_topicCountPosition]++;
        return true;
    }

    public BlockReceipts Build(int blockNumber, int txCount)
    {
        BlockReceipts result = new(blockNumber, txCount, _buffer, _length);
        _buffer = [];
        _length = 0;
        return result;
    }

    public void Dispose()
    {
        if (_buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _length = 0;
    }

    private Span<byte> Reserve(int size)
    {
        int length = _length + size;
        if (length > _buffer.Length)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, Math.Max(_buffer.Length * 2, MinCapacity)));
            _buffer.AsSpan(0, _length).CopyTo(buffer);
            if (_buffer.Length > 0)
                ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = buffer;
        }

        Span<byte> reserved = _buffer.AsSpan(_length, size);
        _length = length;
        return reserved;
    }
}
