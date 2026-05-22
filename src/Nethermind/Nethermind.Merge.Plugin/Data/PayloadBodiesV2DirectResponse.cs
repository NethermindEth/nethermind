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
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Wraps payload body V2 results and writes JSON directly into a <see cref="PipeWriter"/>.</summary>
public sealed class PayloadBodiesV2DirectResponse : IStreamableResult, IReadOnlyList<ExecutionPayloadBodyV2Result?>, IDisposable
{
    private readonly PayloadBody?[] _items;
    private bool _disposed;

    internal PayloadBodiesV2DirectResponse(PayloadBody?[] items) => _items = items;

    public int Count => _items.Length;

    public ExecutionPayloadBodyV2Result? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_items.Length) throw new ArgumentOutOfRangeException(nameof(index));

            return _items[index] is { } item ? item.ToResult() : null;
        }
    }

    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        StreamableResultWriter.WriteArrayAsync(writer, _items.Length, new ItemWriter(_items), cancellationToken);

    public IEnumerator<ExecutionPayloadBodyV2Result?> GetEnumerator()
    {
        for (int i = 0, count = _items.Length; i < count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeItems(_items);
    }

    internal static PayloadBody CreatePayloadBody(
        IReadOnlyList<Transaction> transactions,
        IReadOnlyList<Withdrawal>? withdrawals,
        MemoryManager<byte>? blockAccessList)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        try
        {
            byte[][] encodedTransactions = new byte[transactions.Count][];
            for (int i = 0, count = encodedTransactions.Length; i < count; i++)
            {
                encodedTransactions[i] = Rlp.Encode(transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
            }

            return new PayloadBody(encodedTransactions, withdrawals, blockAccessList);
        }
        catch
        {
            (blockAccessList as IDisposable)?.Dispose();
            throw;
        }
    }

    internal static void DisposeItems(PayloadBody?[] items)
    {
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] is { } item)
            {
                item.Dispose();
            }
        }
    }

    private readonly struct ItemWriter(PayloadBody?[] items) : IJsonArrayItemWriter
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

    internal readonly struct PayloadBody(IReadOnlyList<byte[]> transactions, IReadOnlyList<Withdrawal>? withdrawals, MemoryManager<byte>? blockAccessList) : IDisposable
    {
        public void WriteTo(PipeWriter writer)
        {
            writer.Write("{\"transactions\":"u8);
            PayloadBodiesDirectResponseWriter.WriteTransactions(writer, transactions);
            writer.Write(",\"withdrawals\":"u8);
            PayloadBodiesDirectResponseWriter.WriteWithdrawals(writer, withdrawals);

            if (blockAccessList is not null)
            {
                writer.Write(",\"blockAccessList\":"u8);
                PayloadBodiesDirectResponseWriter.WriteHexString(writer, blockAccessList.Memory.Span, chunked: true);
            }

            writer.Write("}"u8);
        }

        public ExecutionPayloadBodyV2Result ToResult() =>
            ExecutionPayloadBodyV2Result.FromEncodedTransactions(
                transactions,
                withdrawals,
                blockAccessList?.Memory.ToArray());

        public void Dispose() => (blockAccessList as IDisposable)?.Dispose();
    }
}
