// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Wraps payload body V1 results and writes JSON directly into a <see cref="PipeWriter"/>.</summary>
public sealed class PayloadBodiesV1DirectResponse(IReadOnlyList<ExecutionPayloadBodyV1Result?> items)
    : IStreamableResult, IReadOnlyList<ExecutionPayloadBodyV1Result?>
{
    private readonly IReadOnlyList<ExecutionPayloadBodyV1Result?> _items = items;

    public int Count => _items.Count;

    public ExecutionPayloadBodyV1Result? this[int index] => _items[index];

    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        StreamableResultWriter.WriteArrayAsync(writer, _items.Count, new ItemWriter(_items), cancellationToken);

    public IEnumerator<ExecutionPayloadBodyV1Result?> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private readonly struct ItemWriter(IReadOnlyList<ExecutionPayloadBodyV1Result?> items) : IJsonArrayItemWriter
    {
        public void WriteItem(PipeWriter writer, int index)
        {
            ExecutionPayloadBodyV1Result? item = items[index];
            if (item is null)
            {
                writer.Write("null"u8);
                return;
            }

            if (item.SourceTransactions is { } sourceTransactions)
            {
                PayloadBodiesDirectResponseWriter.WritePayloadBody(writer, sourceTransactions, item.Withdrawals);
                return;
            }

            PayloadBodiesDirectResponseWriter.WritePayloadBody(writer, item.EncodedTransactions, item.Withdrawals);
        }
    }
}

internal static class PayloadBodiesDirectResponseWriter
{
    internal const int HexChunkThreshold = 64 * 1024;

    public static byte[][] EncodeTransactions(Transaction[] transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        byte[][] encodedTransactions = new byte[transactions.Length][];
        for (int i = 0, count = encodedTransactions.Length; i < count; i++)
        {
            encodedTransactions[i] = Rlp.Encode(transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
        }

        return encodedTransactions;
    }

    public static void WritePayloadBody(
        IBufferWriter<byte> writer,
        byte[][] transactions,
        Withdrawal[]? withdrawals,
        MemoryManager<byte>? blockAccessList = null)
    {
        writer.Write("{\"transactions\":"u8);
        WriteTransactions(writer, transactions);
        WritePayloadBodySuffix(writer, withdrawals, blockAccessList);
    }

    public static void WritePayloadBody(
        IBufferWriter<byte> writer,
        Transaction[] transactions,
        Withdrawal[]? withdrawals,
        MemoryManager<byte>? blockAccessList = null)
    {
        writer.Write("{\"transactions\":"u8);
        WriteTransactions(writer, transactions);
        WritePayloadBodySuffix(writer, withdrawals, blockAccessList);
    }

    private static void WritePayloadBodySuffix(
        IBufferWriter<byte> writer,
        Withdrawal[]? withdrawals,
        MemoryManager<byte>? blockAccessList)
    {
        writer.Write(",\"withdrawals\":"u8);
        WriteWithdrawals(writer, withdrawals);
        if (blockAccessList is not null)
        {
            writer.Write(",\"blockAccessList\":"u8);
            HexWriter.WriteHexString(writer, blockAccessList.Memory.Span, chunked: true);
        }

        writer.Write("}"u8);
    }

    public static void WriteTransactions(IBufferWriter<byte> writer, byte[][] transactions)
    {
        writer.Write("["u8);

        for (int i = 0, count = transactions.Length; i < count; i++)
        {
            if (i > 0) writer.Write(","u8);

            HexWriter.WriteHexString(writer, transactions[i], chunked: true);
        }

        writer.Write("]"u8);
    }

    public static void WriteTransactions(IBufferWriter<byte> writer, Transaction[] transactions)
    {
        writer.Write("["u8);

        for (int i = 0, count = transactions.Length; i < count; i++)
        {
            if (i > 0) writer.Write(","u8);
            WriteTransaction(writer, transactions[i]);
        }

        writer.Write("]"u8);
    }

    public static void WriteTransaction(IBufferWriter<byte> writer, Transaction transaction)
    {
        int length = TxDecoder.Instance.GetLength(transaction, RlpBehaviors.SkipTypedWrapping);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            RlpStream stream = new(buffer);
            TxDecoder.Instance.Encode(stream, transaction, RlpBehaviors.SkipTypedWrapping);
            HexWriter.WriteHexString(writer, buffer.AsSpan(0, length), chunked: length > HexChunkThreshold);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static void WriteWithdrawals(IBufferWriter<byte> writer, Withdrawal[]? withdrawals)
    {
        if (withdrawals is null)
        {
            writer.Write("null"u8);
            return;
        }

        WriteWithdrawalArray(writer, withdrawals);
    }

    public static void WriteWithdrawalArray(IBufferWriter<byte> writer, Withdrawal[] withdrawals)
    {
        writer.Write("["u8);

        for (int i = 0, count = withdrawals.Length; i < count; i++)
        {
            if (i > 0) writer.Write(","u8);
            WriteWithdrawal(writer, withdrawals[i]);
        }

        writer.Write("]"u8);
    }

    internal static void WriteWithdrawal(IBufferWriter<byte> writer, Withdrawal withdrawal)
    {
        writer.Write("{\"index\":"u8);
        HexWriter.WriteUlongHexString(writer, withdrawal.Index);
        writer.Write(",\"validatorIndex\":"u8);
        HexWriter.WriteUlongHexString(writer, withdrawal.ValidatorIndex);
        writer.Write(",\"address\":"u8);

        Address? address = withdrawal.Address;
        if (address is null)
        {
            writer.Write("null"u8);
        }
        else
        {
            HexWriter.WriteHexString(writer, address.Bytes, chunked: false);
        }

        writer.Write(",\"amount\":"u8);
        HexWriter.WriteUlongHexString(writer, withdrawal.AmountInGwei);
        writer.Write("}"u8);
    }
}
