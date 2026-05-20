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

/// <summary>
/// Wraps payload body V2 results and writes JSON directly into a <see cref="PipeWriter"/>.
/// </summary>
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

    public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        writer.Write("["u8);

        int count = _items.Length;
        for (int i = 0; i < count; i++)
        {
            if (i > 0) writer.Write(","u8);

            PayloadBody? item = _items[i];
            if (item is null)
            {
                writer.Write("null"u8);
            }
            else
            {
                item.GetValueOrDefault().WriteTo(writer);
            }

            if (await StreamableResultWriter.FlushIfNeededAsync(writer, cancellationToken))
            {
                return;
            }
        }

        writer.Write("]"u8);
    }

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

    internal readonly struct PayloadBody : IDisposable
    {
        private readonly IReadOnlyList<byte[]> _transactions;
        private readonly IReadOnlyList<Withdrawal>? _withdrawals;
        private readonly MemoryManager<byte>? _blockAccessList;

        internal PayloadBody(
            IReadOnlyList<byte[]> transactions,
            IReadOnlyList<Withdrawal>? withdrawals,
            MemoryManager<byte>? blockAccessList)
        {
            _transactions = transactions;
            _withdrawals = withdrawals;
            _blockAccessList = blockAccessList;
        }

        public void WriteTo(PipeWriter writer)
        {
            writer.Write("{\"transactions\":"u8);
            PayloadBodiesDirectResponseWriter.WriteTransactions(writer, _transactions);
            writer.Write(",\"withdrawals\":"u8);
            PayloadBodiesDirectResponseWriter.WriteWithdrawals(writer, _withdrawals);

            if (_blockAccessList is not null)
            {
                writer.Write(",\"blockAccessList\":"u8);
                PayloadBodiesDirectResponseWriter.WriteHexString(writer, _blockAccessList.Memory.Span, chunked: true);
            }

            writer.Write("}"u8);
        }

        public ExecutionPayloadBodyV2Result ToResult() =>
            ExecutionPayloadBodyV2Result.FromEncodedTransactions(
                _transactions,
                _withdrawals,
                _blockAccessList?.Memory.ToArray());

        public void Dispose() => (_blockAccessList as IDisposable)?.Dispose();
    }
}
