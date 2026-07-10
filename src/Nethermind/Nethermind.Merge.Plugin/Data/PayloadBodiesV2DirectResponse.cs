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
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;

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
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_items.Length, nameof(index));
            return _items[index]?.ToResult();
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
        Transaction[] transactions,
        Withdrawal[]? withdrawals,
        MemoryManager<byte>? blockAccessList)
    {
        try
        {
            return new PayloadBody(transactions, withdrawals, blockAccessList);
        }
        catch
        {
            blockAccessList?.TryDispose();
            throw;
        }
    }

    internal static PayloadBody CreatePayloadBody(
        byte[] blockRlp,
        MemoryManager<byte>? blockAccessList)
    {
        try
        {
            return new PayloadBody(blockRlp, blockAccessList);
        }
        catch
        {
            blockAccessList?.TryDispose();
            throw;
        }
    }

    internal static void DisposeItems(PayloadBody?[] items)
    {
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] is { } item) item.Dispose();
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

    internal readonly struct PayloadBody : IDisposable
    {
        private readonly Transaction[]? _transactions;
        private readonly byte[]? _blockRlp;
        private readonly Withdrawal[]? _withdrawals;
        private readonly MemoryManager<byte>? _blockAccessList;

        public PayloadBody(Transaction[] transactions, Withdrawal[]? withdrawals, MemoryManager<byte>? blockAccessList)
        {
            _transactions = transactions;
            _blockRlp = null;
            _withdrawals = withdrawals;
            _blockAccessList = blockAccessList;
        }

        public PayloadBody(byte[] blockRlp, MemoryManager<byte>? blockAccessList)
        {
            _transactions = null;
            _blockRlp = blockRlp;
            _withdrawals = null;
            _blockAccessList = blockAccessList;
        }

        public void WriteTo(PipeWriter writer)
        {
            if (_blockRlp is { } blockRlp)
            {
                PayloadBodiesDirectResponseWriter.WritePayloadBody(writer, blockRlp, _blockAccessList);
            }
            else
            {
                PayloadBodiesDirectResponseWriter.WritePayloadBody(writer, _transactions!, _withdrawals, _blockAccessList);
            }
        }

        public ExecutionPayloadBodyV2Result ToResult()
        {
            if (_blockRlp is { } blockRlp)
            {
                (byte[][] transactions, Withdrawal[]? withdrawals) = PayloadBodiesDirectResponseWriter.DecodePayloadBody(blockRlp);
                ExecutionPayloadBodyV2Result result = new([], withdrawals, _blockAccessList?.Memory.ToArray())
                {
                    Transactions = transactions
                };
                return result;
            }

            return new ExecutionPayloadBodyV2Result(_transactions!, _withdrawals, _blockAccessList?.Memory.ToArray());
        }

        public void Dispose() => _blockAccessList?.TryDispose();
    }
}
