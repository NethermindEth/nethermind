// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core.Test.Encoding;

/// <summary>Hand-builds receipt RLP, so a log count can be declared without materialising the logs.</summary>
public static class ReceiptRlpBuilder
{
    // Arbitrary - no test reads it back, it only has to be a well-formed cumulative gas item.
    private const ulong GasUsedTotal = 21000;

    /// <summary>A log count comfortably under <see cref="RlpLimit.ReceiptLogs"/>, but far more logs than the bytes declaring them could hold.</summary>
    public const int UnbackedLogCount = 1_000;

    /// <summary>The smallest log the decoders accept: an address, no topics and no data.</summary>
    public static LogEntry MinimalLog() => new(Address.Zero, [], []);

    /// <summary>Builds a log list of identical entries.</summary>
    /// <param name="logCount">Number of items to write into the receipt's log sequence.</param>
    /// <param name="log">
    /// Log to repeat, or <c>null</c> for one-byte <c>0xC0</c> placeholders - the cheapest item a
    /// count can be built from, and one no decoder accepts as a log.
    /// </param>
    public static LogEntry?[] Repeat(int logCount, LogEntry? log = null)
    {
        LogEntry?[] logs = new LogEntry?[logCount];
        Array.Fill(logs, log);
        return logs;
    }

    /// <summary>Builds an eth/63 receipt body: status, cumulative gas, bloom, then the logs.</summary>
    public static byte[] EncodeReceipt(ReadOnlySpan<LogEntry?> logs) =>
        Encode(logs, txType: null, includeBloom: true);

    /// <summary>Builds an eth/69 receipt body: tx type, status, cumulative gas, then the logs - no bloom.</summary>
    public static byte[] EncodeReceipt69(TxType txType, ReadOnlySpan<LogEntry?> logs) =>
        Encode(logs, txType, includeBloom: false);

    private static byte[] Encode(ReadOnlySpan<LogEntry?> logs, TxType? txType, bool includeBloom)
    {
        LogEntryDecoder logEntryDecoder = LogEntryDecoder.Instance;
        int logsLength = 0;
        foreach (LogEntry? log in logs)
        {
            logsLength += logEntryDecoder.GetLength(log);
        }

        int contentLength = (txType is null ? 0 : Rlp.LengthOf((byte)txType.Value))
                            + Rlp.LengthOf((byte)1)
                            + Rlp.LengthOf(GasUsedTotal)
                            + (includeBloom ? Rlp.LengthOf(Bloom.Empty) : 0)
                            + Rlp.LengthOfSequence(logsLength);

        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(bytes);
        writer.StartSequence(contentLength);
        if (txType is not null)
        {
            writer.Encode((byte)txType.Value);
        }

        writer.Encode((byte)1);
        writer.Encode(GasUsedTotal);
        if (includeBloom)
        {
            writer.Encode(Bloom.Empty);
        }

        writer.StartSequence(logsLength);
        foreach (LogEntry? log in logs)
        {
            logEntryDecoder.Encode(ref writer, log);
        }

        return bytes;
    }
}
