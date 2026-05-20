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

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Wraps payload body V1 results and writes JSON directly into a <see cref="PipeWriter"/>.
/// </summary>
public sealed class PayloadBodiesV1DirectResponse(IReadOnlyList<ExecutionPayloadBodyV1Result?> items)
    : IStreamableResult, IReadOnlyList<ExecutionPayloadBodyV1Result?>, IDisposable
{
    private readonly IReadOnlyList<ExecutionPayloadBodyV1Result?> _items = items;

    public int Count => _items.Count;

    public ExecutionPayloadBodyV1Result? this[int index] => _items[index];

    public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        writer.Write("["u8);

        int count = _items.Count;
        for (int i = 0; i < count; i++)
        {
            if (i > 0) writer.Write(","u8);

            ExecutionPayloadBodyV1Result? item = _items[i];
            if (item is null)
            {
                writer.Write("null"u8);
            }
            else
            {
                PayloadBodiesDirectResponseWriter.WritePayloadBodyV1(writer, item.Transactions, item.Withdrawals);
            }

            if (await StreamableResultWriter.FlushIfNeededAsync(writer, cancellationToken))
            {
                return;
            }
        }

        writer.Write("]"u8);
    }

    public IEnumerator<ExecutionPayloadBodyV1Result?> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() { }
}

internal static class PayloadBodiesDirectResponseWriter
{
    public static void WritePayloadBodyV1(
        PipeWriter writer,
        IReadOnlyList<byte[]> transactions,
        IReadOnlyList<Withdrawal>? withdrawals)
    {
        writer.Write("{\"transactions\":"u8);
        WriteTransactions(writer, transactions);
        writer.Write(",\"withdrawals\":"u8);
        WriteWithdrawals(writer, withdrawals);
        writer.Write("}"u8);
    }

    public static void WriteTransactions(PipeWriter writer, IReadOnlyList<byte[]> transactions)
    {
        writer.Write("["u8);

        for (int i = 0, count = transactions.Count; i < count; i++)
        {
            if (i > 0) writer.Write(","u8);

            writer.Write("\"0x"u8);
            HexWriter.WriteHexChunked(writer, transactions[i]);
            writer.Write("\""u8);
        }

        writer.Write("]"u8);
    }

    public static void WriteWithdrawals(PipeWriter writer, IReadOnlyList<Withdrawal>? withdrawals)
    {
        if (withdrawals is null)
        {
            writer.Write("null"u8);
            return;
        }

        writer.Write("["u8);

        for (int i = 0, count = withdrawals.Count; i < count; i++)
        {
            if (i > 0) writer.Write(","u8);
            WriteWithdrawal(writer, withdrawals[i]);
        }

        writer.Write("]"u8);
    }

    public static void WriteHexString(PipeWriter writer, ReadOnlySpan<byte> bytes, bool chunked)
    {
        writer.Write("\"0x"u8);

        if (chunked)
        {
            HexWriter.WriteHexChunked(writer, bytes);
        }
        else
        {
            HexWriter.WriteHexSmall(writer, bytes);
        }

        writer.Write("\""u8);
    }

    private static void WriteWithdrawal(PipeWriter writer, Withdrawal withdrawal)
    {
        writer.Write("{\"index\":"u8);
        WriteUlongHexString(writer, withdrawal.Index);
        writer.Write(",\"validatorIndex\":"u8);
        WriteUlongHexString(writer, withdrawal.ValidatorIndex);
        writer.Write(",\"address\":"u8);

        Address? address = withdrawal.Address;
        if (address is null)
        {
            writer.Write("null"u8);
        }
        else
        {
            WriteHexString(writer, address.Bytes, chunked: false);
        }

        writer.Write(",\"amount\":"u8);
        WriteUlongHexString(writer, withdrawal.AmountInGwei);
        writer.Write("}"u8);
    }

    private static void WriteUlongHexString(PipeWriter writer, ulong value)
    {
        if (value == 0)
        {
            writer.Write("\"0x0\""u8);
            return;
        }

        Span<byte> buffer = writer.GetSpan(20);
        buffer[0] = (byte)'"';
        buffer[1] = (byte)'0';
        buffer[2] = (byte)'x';
        value.TryFormat(buffer[3..], out int bytesWritten, "x");
        buffer[bytesWritten + 3] = (byte)'"';
        writer.Advance(bytesWritten + 4);
    }
}
