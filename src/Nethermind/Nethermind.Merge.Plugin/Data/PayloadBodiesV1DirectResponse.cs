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
public sealed class PayloadBodiesV1DirectResponse : IStreamableResult, IReadOnlyList<ExecutionPayloadBodyV1Result?>
{
    private readonly ExecutionPayloadBodyV1Result?[]? _items;
    private readonly PayloadBody?[]? _payloadBodies;

    public PayloadBodiesV1DirectResponse(ExecutionPayloadBodyV1Result?[] items) => _items = items;

    internal PayloadBodiesV1DirectResponse(PayloadBody?[] payloadBodies) => _payloadBodies = payloadBodies;

    public int Count => _payloadBodies?.Length ?? _items!.Length;

    public ExecutionPayloadBodyV1Result? this[int index] => _payloadBodies is { } payloadBodies
        ? payloadBodies[index]?.ToResult()
        : _items![index];

    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        if (_payloadBodies is { } payloadBodies)
        {
            return StreamableResultWriter.WriteArrayAsync(writer, payloadBodies.Length, new RawItemWriter(payloadBodies), cancellationToken);
        }

        ExecutionPayloadBodyV1Result?[] items = _items!;
        return StreamableResultWriter.WriteArrayAsync(writer, items.Length, new ItemWriter(items), cancellationToken);
    }

    public IEnumerator<ExecutionPayloadBodyV1Result?> GetEnumerator()
    {
        for (int i = 0, count = Count; i < count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static PayloadBody CreatePayloadBody(byte[] blockRlp) => new(blockRlp);

    private readonly struct ItemWriter(ExecutionPayloadBodyV1Result?[] items) : IJsonArrayItemWriter
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

    private readonly struct RawItemWriter(PayloadBody?[] items) : IJsonArrayItemWriter
    {
        public void WriteItem(PipeWriter writer, int index)
        {
            PayloadBody? item = items[index];
            if (item is null)
            {
                writer.Write("null"u8);
                return;
            }

            item.GetValueOrDefault().WriteTo(writer);
        }
    }

    internal readonly struct PayloadBody(byte[] blockRlp)
    {
        public void WriteTo(PipeWriter writer) =>
            PayloadBodiesDirectResponseWriter.WritePayloadBody(writer, blockRlp);

        public ExecutionPayloadBodyV1Result ToResult()
        {
            Withdrawal[]? withdrawals = PayloadBodiesDirectResponseWriter.DecodeWithdrawals(blockRlp);
            ExecutionPayloadBodyV1Result result = new([], withdrawals);
            result.Transactions = PayloadBodiesDirectResponseWriter.GetTransactionsFromBlockRlp(blockRlp);
            return result;
        }
    }
}

internal static class PayloadBodiesDirectResponseWriter
{
    internal const int HexChunkThreshold = 64 * 1024;
    private static readonly WithdrawalDecoder WithdrawalDecoder = new();

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

    public static void WritePayloadBody(
        IBufferWriter<byte> writer,
        byte[] blockRlp,
        MemoryManager<byte>? blockAccessList = null)
    {
        Rlp.ValueDecoderContext ctx = new(blockRlp);
        int blockEnd = ctx.ReadSequenceLength() + ctx.Position;

        writer.Write("{\"transactions\":"u8);
        // Payload body responses do not include the header. After streaming the
        // transactions list, the same RLP cursor continues at the ommers item.
        ctx.SkipItem(); // header
        WriteTransactionsFromBlockRlp(writer, ref ctx);

        writer.Write(",\"withdrawals\":"u8);
        ctx.SkipItem(); // ommers
        WriteWithdrawals(writer, ctx.Position == blockEnd ? null : WithdrawalDecoder.DecodeArray(ref ctx));

        if (blockAccessList is not null)
        {
            writer.Write(",\"blockAccessList\":"u8);
            HexWriter.WriteHexString(writer, blockAccessList.Memory.Span, chunked: true);
        }

        writer.Write("}"u8);
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

        RlpStream stream = new(buffer);
        TxDecoder.Instance.Encode(stream, transaction, RlpBehaviors.SkipTypedWrapping);
        HexWriter.WriteHexString(writer, buffer.AsSpan(0, length), chunked: length > HexChunkThreshold);
        ArrayPool<byte>.Shared.Return(buffer);
    }

    public static Withdrawal[]? DecodeWithdrawals(byte[] blockRlp)
    {
        Rlp.ValueDecoderContext ctx = new(blockRlp);
        int blockEnd = ctx.ReadSequenceLength() + ctx.Position;
        // Withdrawals are the fourth top-level block item. Skip earlier items
        // without decoding them because payload body responses only need
        // transactions and withdrawals, and transactions are streamed separately.
        ctx.SkipItem(); // header
        ctx.SkipItem(); // transactions
        ctx.SkipItem(); // ommers
        return ctx.Position == blockEnd
            ? null
            : WithdrawalDecoder.DecodeArray(ref ctx);
    }

    public static byte[][] GetTransactionsFromBlockRlp(byte[] blockRlp)
    {
        Rlp.ValueDecoderContext ctx = CreateTransactionContext(blockRlp, out int txsEnd);
        int count = ctx.PeekNumberOfItemsRemaining(txsEnd);
        byte[][] transactions = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            transactions[i] = ReadTransactionBytes(ref ctx).ToArray();
        }

        return transactions;
    }

    public static void WriteTransactionsFromBlockRlp(IBufferWriter<byte> writer, byte[] blockRlp)
    {
        Rlp.ValueDecoderContext ctx = CreateTransactionContext(blockRlp, out int txsEnd);
        WriteTransactionsFromBlockRlp(writer, ref ctx, txsEnd);
    }

    private static void WriteTransactionsFromBlockRlp(IBufferWriter<byte> writer, ref Rlp.ValueDecoderContext ctx)
    {
        int txsEnd = ctx.ReadSequenceLength() + ctx.Position;
        WriteTransactionsFromBlockRlp(writer, ref ctx, txsEnd);
    }

    private static void WriteTransactionsFromBlockRlp(IBufferWriter<byte> writer, ref Rlp.ValueDecoderContext ctx, int txsEnd)
    {
        writer.Write("["u8);

        for (int i = 0; ctx.Position < txsEnd; i++)
        {
            if (i > 0) writer.Write(","u8);
            ReadOnlySpan<byte> transaction = ReadTransactionBytes(ref ctx);
            HexWriter.WriteHexString(writer, transaction, chunked: transaction.Length > HexChunkThreshold);
        }

        writer.Write("]"u8);
    }

    private static Rlp.ValueDecoderContext CreateTransactionContext(byte[] blockRlp, out int txsEnd)
    {
        Rlp.ValueDecoderContext ctx = new(blockRlp);
        ctx.ReadSequenceLength();
        // Transactions are the second top-level block item; skip the header
        // so the returned context is positioned on the transactions list.
        ctx.SkipItem(); // header
        // ReadSequenceLength advances ctx.Position past the list prefix; txsEnd is read after that
        // advance (left-to-right evaluation), so it points to the end of the transactions list.
        txsEnd = ctx.ReadSequenceLength() + ctx.Position;
        return ctx;
    }

    private static ReadOnlySpan<byte> ReadTransactionBytes(ref Rlp.ValueDecoderContext ctx)
    {
        ReadOnlySpan<byte> transaction = ctx.PeekNextItem();
        // Keep a span over the current transaction, then advance the cursor
        // so the caller can continue iterating without decoding the transaction.
        ctx.SkipItem(); // current transaction
        // transaction[0] is safe: a transaction in a validated block is never an empty RLP item.
        if (transaction[0] >= Rlp.OfEmptyList[0])
        {
            return transaction;
        }

        // Typed transactions are stored as an RLP string containing type || payload;
        // Engine payload bodies expect the string content, not the RLP wrapper.
        Rlp.ValueDecoderContext transactionContext = new(transaction);
        (_, int contentLength) = transactionContext.PeekPrefixAndContentLength();
        return transaction[^contentLength..];
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
