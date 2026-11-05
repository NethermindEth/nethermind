// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Data;

/// <summary>A block's receipts for RPC, built from the stored receipts on demand.</summary>
/// <remarks>
/// <see cref="ReceiptsForRpcConverterFactory"/> writes the receipts one at a time through a single reused
/// <typeparamref name="TReceipt"/>; the indexer and the enumerator build a new one on each access.
/// </remarks>
/// <param name="receipts">The block's receipts.</param>
/// <param name="transactions">The block's transactions.</param>
/// <param name="logIndexPerReceipt">Whether <c>logIndex</c> counts within the receipt (EIP-8116) rather than across the block.</param>
/// <param name="populate">Fills one RPC receipt.</param>
[JsonConverter(typeof(ReceiptsForRpcConverterFactory))]
public sealed class ReceiptsForRpc<TReceipt>(TxReceipt[] receipts, Transaction[] transactions, bool logIndexPerReceipt, ReceiptsForRpc<TReceipt>.Populator populate)
    : IReadOnlyList<TReceipt>
    where TReceipt : ReceiptForRpc, new()
{
    /// <summary>Fills <paramref name="target"/> from <paramref name="receipt"/> of <paramref name="transaction"/>.</summary>
    public delegate void Populator(TReceipt target, TxReceipt receipt, Transaction transaction, int logIndexStart);

    private readonly TxReceipt[] _receipts = receipts;
    private readonly Transaction[] _transactions = transactions;
    private readonly bool _logIndexPerReceipt = logIndexPerReceipt;
    private readonly Populator _populate = populate;

    // Reads every receipt, so a missing one fails while the response is built rather than part-way through writing it.
    private readonly bool _positionalIndexes = HasPositionalIndexes(receipts);

    public int Count { get; } = Math.Min(receipts.Length, transactions.Length);

    public TReceipt this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            int logIndexStart = _logIndexPerReceipt ? 0 : _receipts.GetBlockLogFirstIndex(_receipts[index].Index);
            return Build(index, logIndexStart, new TReceipt());
        }
    }

    public IEnumerator<TReceipt> GetEnumerator()
    {
        int runningLogIndex = 0;
        for (int i = 0; i < Count; i++)
        {
            yield return Build(i, NextLogIndexStart(i, ref runningLogIndex), new TReceipt());
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Write(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        JsonTypeInfo<TReceipt> typeInfo = TypeInfoJsonSerializer.GetTypeInfo<TReceipt>(options);
        TReceipt reused = new();
        int runningLogIndex = 0;
        writer.WriteStartArray();
        for (int i = 0; i < Count; i++)
        {
            JsonSerializer.Serialize(writer, Build(i, NextLogIndexStart(i, ref runningLogIndex), reused), typeInfo);
        }

        writer.WriteEndArray();
    }

    private TReceipt Build(int index, int logIndexStart, TReceipt target)
    {
        _populate(target, _receipts[index], _transactions[index], logIndexStart);
        return target;
    }

    // A running sum equals the per-receipt scan only when every index is its position.
    private int NextLogIndexStart(int index, ref int runningLogIndex)
    {
        TxReceipt receipt = _receipts[index];
        int logIndexStart = _logIndexPerReceipt ? 0 : _positionalIndexes ? runningLogIndex : _receipts.GetBlockLogFirstIndex(receipt.Index);
        runningLogIndex += receipt.Logs?.Length ?? 0;
        return logIndexStart;
    }

    private static bool HasPositionalIndexes(TxReceipt[] receipts)
    {
        for (int i = 0; i < receipts.Length; i++)
        {
            if (receipts[i].Index != i) return false;
        }

        return true;
    }
}

/// <summary>Creates the writer for <see cref="ReceiptsForRpc{TReceipt}"/>.</summary>
/// <remarks>Public because source-generated contexts in other assemblies instantiate the converter <see cref="ReceiptsForRpc{TReceipt}"/> names.</remarks>
public sealed class ReceiptsForRpcConverterFactory : JsonConverterFactory
{
    /// <inheritdoc/>
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ReceiptsForRpc<>);

    /// <inheritdoc/>
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(ReceiptsForRpcConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;

    private sealed class ReceiptsForRpcConverter<TReceipt> : JsonConverter<ReceiptsForRpc<TReceipt>>
        where TReceipt : ReceiptForRpc, new()
    {
        public override ReceiptsForRpc<TReceipt> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, ReceiptsForRpc<TReceipt> value, JsonSerializerOptions options) =>
            value.Write(writer, options);
    }
}
