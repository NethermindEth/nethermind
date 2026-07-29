// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

    public ExecutionPayloadBodyV1Result? this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            return _payloadBodies is { } payloadBodies ? payloadBodies[index]?.ToResult() : _items![index];
        }
    }

    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        _payloadBodies is { } payloadBodies
            ? StreamableResultWriter.WriteArrayAsync(writer, payloadBodies.Length, new RawItemWriter(payloadBodies), cancellationToken)
            : StreamableResultWriter.WriteArrayAsync(writer, _items!.Length, new ItemWriter(_items!), cancellationToken);

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<ExecutionPayloadBodyV1Result?> IEnumerable<ExecutionPayloadBodyV1Result?>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static PayloadBody CreatePayloadBody(byte[] blockRlp) => new(blockRlp);

    public struct Enumerator(PayloadBodiesV1DirectResponse response) : IEnumerator<ExecutionPayloadBodyV1Result?>
    {
        private int _index = -1;

        public bool MoveNext() => ++_index < response.Count;
        public void Reset() => _index = -1;
        public readonly ExecutionPayloadBodyV1Result? Current => response[_index];
        readonly object? IEnumerator.Current => Current;
        public readonly void Dispose() { }
    }

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
            (byte[][] transactions, Withdrawal[]? withdrawals) = PayloadBodiesDirectResponseWriter.DecodePayloadBody(blockRlp);
            ExecutionPayloadBodyV1Result result = new([], withdrawals);
            result.Transactions = transactions;
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
        MemoryManager<byte>? blockAccessList = null,
        bool includeBlockAccessList = false)
    {
        writer.Write("{\"transactions\":"u8);
        WriteTransactions(writer, transactions);
        WritePayloadBodySuffix(writer, withdrawals, blockAccessList, includeBlockAccessList);
    }

    public static void WritePayloadBody(
        IBufferWriter<byte> writer,
        Transaction[] transactions,
        Withdrawal[]? withdrawals,
        MemoryManager<byte>? blockAccessList = null,
        bool includeBlockAccessList = false)
    {
        writer.Write("{\"transactions\":"u8);
        WriteTransactions(writer, transactions);
        WritePayloadBodySuffix(writer, withdrawals, blockAccessList, includeBlockAccessList);
    }

    public static void WritePayloadBody(
        IBufferWriter<byte> writer,
        byte[] blockRlp,
        MemoryManager<byte>? blockAccessList = null,
        bool includeBlockAccessList = false)
    {
        RlpReader ctx = new(blockRlp);
        int blockEnd = ctx.ReadSequenceLength() + ctx.Position;

        // Keep this field order aligned with BlockBodyDecoder.DecodeUnwrapped; this path streams without materializing BlockBody.
        ctx.SkipItem(); // header
        writer.Write("{\"transactions\":"u8);
        WriteTransactionsFromBlockRlp(writer, ref ctx);

        ctx.SkipItem(); // uncles
        writer.Write(",\"withdrawals\":"u8);
        WriteWithdrawalsFromBlockRlp(writer, ref ctx, blockEnd);

        WriteBlockAccessList(writer, blockAccessList, includeBlockAccessList);
        writer.Write("}"u8);
    }

    private static void WritePayloadBodySuffix(
        IBufferWriter<byte> writer,
        Withdrawal[]? withdrawals,
        MemoryManager<byte>? blockAccessList,
        bool includeBlockAccessList)
    {
        writer.Write(",\"withdrawals\":"u8);
        WriteWithdrawals(writer, withdrawals);
        WriteBlockAccessList(writer, blockAccessList, includeBlockAccessList);
        writer.Write("}"u8);
    }

    /// <summary>V2 bodies always carry the key (literal <c>null</c> when absent); V1 bodies omit it.</summary>
    private static void WriteBlockAccessList(
        IBufferWriter<byte> writer,
        MemoryManager<byte>? blockAccessList,
        bool includeBlockAccessList)
    {
        if (!includeBlockAccessList)
        {
            return;
        }

        writer.Write(",\"blockAccessList\":"u8);
        if (blockAccessList is null)
        {
            writer.Write("null"u8);
        }
        else
        {
            HexWriter.WriteHexString(writer, blockAccessList.Memory.Span, chunked: true);
        }
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
            RlpWriter rlpWriter = new(buffer);
            TxDecoder.Instance.Encode(ref rlpWriter, transaction, RlpBehaviors.SkipTypedWrapping);
            HexWriter.WriteHexString(writer, buffer.AsSpan(0, length), chunked: length > HexChunkThreshold);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static (byte[][] Transactions, Withdrawal[]? Withdrawals) DecodePayloadBody(byte[] blockRlp)
    {
        RlpReader ctx = new(blockRlp);
        int blockEnd = ctx.ReadSequenceLength() + ctx.Position;
        ctx.SkipItem(); // header
        byte[][] transactions = GetTransactionsFromBlockRlp(ref ctx);
        ctx.SkipItem(); // uncles
        Withdrawal[]? withdrawals = ctx.Position != blockEnd ? WithdrawalDecoder.DecodeArray(ref ctx) : null;
        return (transactions, withdrawals);
    }

    public static byte[][] GetTransactionsFromBlockRlp(byte[] blockRlp)
    {
        RlpReader reader = CreateTransactionReader(blockRlp, out int txsEnd);
        return GetTransactionsFromBlockRlp(ref reader, txsEnd);
    }

    private static byte[][] GetTransactionsFromBlockRlp(ref RlpReader ctx)
    {
        int txsEnd = ctx.ReadSequenceLength() + ctx.Position;
        return GetTransactionsFromBlockRlp(ref ctx, txsEnd);
    }

    private static byte[][] GetTransactionsFromBlockRlp(ref RlpReader ctx, int txsEnd)
    {
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
        RlpReader reader = CreateTransactionReader(blockRlp, out int txsEnd);
        WriteTransactionsFromBlockRlp(writer, ref reader, txsEnd);
    }

    private static void WriteTransactionsFromBlockRlp(IBufferWriter<byte> writer, ref RlpReader ctx)
    {
        int txsEnd = ctx.ReadSequenceLength() + ctx.Position;
        WriteTransactionsFromBlockRlp(writer, ref ctx, txsEnd);
    }

    private static void WriteTransactionsFromBlockRlp(IBufferWriter<byte> writer, ref RlpReader ctx, int txsEnd)
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

    private static RlpReader CreateTransactionReader(byte[] blockRlp, out int txsEnd)
    {
        RlpReader reader = new(blockRlp);
        reader.ReadSequenceLength();
        reader.SkipItem(); // header
        txsEnd = reader.ReadSequenceLength() + reader.Position;
        return reader;
    }

    private static ReadOnlySpan<byte> ReadTransactionBytes(ref RlpReader ctx)
    {
        ReadOnlySpan<byte> transaction = ctx.PeekNextItem();
        ctx.SkipItem(); // current transaction
        if (transaction[0] >= Rlp.OfEmptyList[0])
        {
            return transaction;
        }

        // Typed transactions are stored as an RLP string containing type || payload;
        // Engine payload bodies expect the string content, not the RLP wrapper.
        RlpReader transactionReader = new(transaction);
        (_, int contentLength) = transactionReader.PeekPrefixAndContentLength();
        return transaction.Slice(transaction.Length - contentLength, contentLength);
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

    private static void WriteWithdrawalsFromBlockRlp(IBufferWriter<byte> writer, ref RlpReader ctx, int blockEnd)
    {
        if (ctx.Position == blockEnd)
        {
            writer.Write("null"u8);
            return;
        }

        int withdrawalsEnd = ctx.ReadSequenceLength() + ctx.Position;
        writer.Write("["u8);

        for (int i = 0; ctx.Position < withdrawalsEnd; i++)
        {
            if (i > 0) writer.Write(","u8);
            WriteWithdrawalFromBlockRlp(writer, ref ctx);
        }

        ctx.Check(withdrawalsEnd);
        writer.Write("]"u8);
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

    private static void WriteWithdrawalFromBlockRlp(IBufferWriter<byte> writer, ref RlpReader ctx)
    {
        int withdrawalEnd = ctx.ReadSequenceLength() + ctx.Position;

        writer.Write("{\"index\":"u8);
        HexWriter.WriteUlongHexString(writer, ctx.DecodeULong());
        writer.Write(",\"validatorIndex\":"u8);
        HexWriter.WriteUlongHexString(writer, ctx.DecodeULong());
        writer.Write(",\"address\":"u8);
        WriteAddressFromBlockRlp(writer, ref ctx);
        writer.Write(",\"amount\":"u8);
        HexWriter.WriteUlongHexString(writer, ctx.DecodeULong());
        writer.Write("}"u8);

        ctx.Check(withdrawalEnd);
    }

    private static void WriteAddressFromBlockRlp(IBufferWriter<byte> writer, ref RlpReader ctx)
    {
        int prefix = ctx.ReadByte();
        if (prefix == Rlp.EmptyByteArrayByte)
        {
            writer.Write("null"u8);
            return;
        }

        if (prefix != Rlp.EmptyByteArrayByte + Address.Size)
        {
            ThrowInvalidWithdrawalAddressPrefix(prefix);
        }

        HexWriter.WriteHexString(writer, ctx.Read(Address.Size), chunked: false);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidWithdrawalAddressPrefix(int prefix) =>
        throw new RlpException($"Invalid withdrawal address prefix {prefix}");
}
