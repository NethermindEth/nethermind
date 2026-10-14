// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Facade.Filters;

/// <summary>
/// Writes a log as the JSON of the <see cref="FilterLog"/> it would become, directly from the stored log, so that
/// a logs response needs no object per log.
/// </summary>
/// <remarks>The output must stay byte-identical to the generated <see cref="FilterLog"/> writer.</remarks>
public static class RawFilterLogJsonWriter
{
    private const int ChunkedDataLength = 1024;
    private const int StackDataLength = 256;
    private static ReadOnlySpan<byte> Zeros => "0000000000000000000000000000000000000000000000000000000000000000"u8;

    public static void Write(IBufferWriter<byte> writer, long logIndex, ulong blockNumber, ulong blockTimestamp,
        Hash256 blockHash, long transactionIndex, Hash256 transactionHash, ref LogEntryStructRef log)
    {
        WriteStart(writer, log.Address.Bytes, blockHash, blockNumber, blockTimestamp);
        WriteData(writer, ref log);
        WriteMiddle(writer, logIndex);
        if (log.Topics is { } topics) WriteTopics(writer, topics);
        else WriteTopics(writer, log.TopicsRlp);
        WriteEnd(writer, transactionHash, transactionIndex);
    }

    public static void Write(IBufferWriter<byte> writer, long logIndex, ulong blockNumber, ulong blockTimestamp,
        Hash256 blockHash, long transactionIndex, Hash256 transactionHash, LogEntry log)
    {
        WriteStart(writer, log.Address.Bytes, blockHash, blockNumber, blockTimestamp);
        HexWriter.WriteHexString(writer, log.Data, log.Data.Length > ChunkedDataLength);
        WriteMiddle(writer, logIndex);
        WriteTopics(writer, log.Topics);
        WriteEnd(writer, transactionHash, transactionIndex);
    }

    private static void WriteStart(IBufferWriter<byte> writer, ReadOnlySpan<byte> address, Hash256 blockHash, ulong blockNumber, ulong blockTimestamp)
    {
        writer.Write("{\"address\":"u8);
        HexWriter.WriteHexString(writer, address, false);
        writer.Write(",\"blockHash\":"u8);
        HexWriter.WriteHexString(writer, blockHash.Bytes, false);
        writer.Write(",\"blockNumber\":"u8);
        HexWriter.WriteUlongHexString(writer, blockNumber);
        writer.Write(",\"blockTimestamp\":"u8);
        HexWriter.WriteUlongHexString(writer, blockTimestamp);
        writer.Write(",\"data\":"u8);
    }

    private static void WriteData(IBufferWriter<byte> writer, ref LogEntryStructRef log)
    {
        int length = log.DataLength;
        if (length <= StackDataLength)
        {
            Span<byte> data = stackalloc byte[length];
            log.CopyDataTo(data);
            HexWriter.WriteHexString(writer, data, false);
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Span<byte> data = rented.AsSpan(0, length);
            log.CopyDataTo(data);
            HexWriter.WriteHexString(writer, data, length > ChunkedDataLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void WriteMiddle(IBufferWriter<byte> writer, long logIndex)
    {
        writer.Write(",\"logIndex\":"u8);
        HexWriter.WriteUlongHexString(writer, (ulong)logIndex);
        writer.Write(",\"removed\":false,\"topics\":["u8);
    }

    private static void WriteTopics(IBufferWriter<byte> writer, Hash256[] topics)
    {
        for (int i = 0; i < topics.Length; i++)
        {
            if (i != 0) writer.Write(","u8);
            HexWriter.WriteHexString(writer, topics[i].Bytes, false);
        }
    }

    /// <summary>
    /// Writes topics from their stored list: full 32-byte hashes, or hashes without their leading zero bytes in
    /// the compact encoding, both right-aligned to 32 bytes.
    /// </summary>
    private static void WriteTopics(IBufferWriter<byte> writer, ReadOnlySpan<byte> topicsRlp)
    {
        RlpReader reader = new(topicsRlp);
        int end = reader.ReadSequenceLength() + reader.Position;
        bool first = true;
        while (reader.Position < end)
        {
            ReadOnlySpan<byte> topic = reader.DecodeByteArraySpan(RlpLimit.L32);
            if (topic.IsEmpty) throw new RlpException("A log topic is missing.");

            if (!first) writer.Write(","u8);
            first = false;
            writer.Write("\"0x"u8);
            writer.Write(Zeros[..((Hash256.Size - topic.Length) * 2)]);
            HexWriter.WriteHexSmall(writer, topic);
            writer.Write("\""u8);
        }

        reader.Check(end);
    }

    private static void WriteEnd(IBufferWriter<byte> writer, Hash256 transactionHash, long transactionIndex)
    {
        writer.Write("],\"transactionHash\":"u8);
        HexWriter.WriteHexString(writer, transactionHash.Bytes, false);
        writer.Write(",\"transactionIndex\":"u8);
        HexWriter.WriteUlongHexString(writer, (ulong)transactionIndex);
        writer.Write("}"u8);
    }
}
