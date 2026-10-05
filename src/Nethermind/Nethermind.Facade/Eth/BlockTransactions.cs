// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth;

/// <summary>The <c>transactions</c> of an RPC block: either their hashes or the full transaction objects.</summary>
/// <remarks>
/// A class rather than a struct so that a block built without transactions keeps omitting the field, as a null array did.
/// An empty array read from JSON says neither which form it was, so <see cref="Hashes"/> and <see cref="Full"/> are both empty.
/// </remarks>
[JsonConverter(typeof(BlockTransactionsConverter))]
public sealed class BlockTransactions
{
    private static readonly BlockTransactions EmptyInstance = new([], []);

    private BlockTransactions(Hash256[]? hashes, TransactionForRpc[]? full)
    {
        Hashes = hashes;
        Full = full;
    }

    /// <summary>The transaction hashes, or <see langword="null"/> for a block carrying full transactions.</summary>
    public Hash256[]? Hashes { get; }

    /// <summary>The full transactions, or <see langword="null"/> for a block carrying hashes.</summary>
    public TransactionForRpc[]? Full { get; }

    public int Length => Full?.Length ?? Hashes!.Length;

    public static BlockTransactions Empty => EmptyInstance;

    public static implicit operator BlockTransactions(Hash256[] hashes) => hashes.Length == 0 ? EmptyInstance : new(hashes, null);

    public static implicit operator BlockTransactions(TransactionForRpc[] transactions) => transactions.Length == 0 ? EmptyInstance : new(null, transactions);
}

/// <summary>Writes hashes or full transactions as the array the RPC block carries, and reads each form back.</summary>
public sealed class BlockTransactionsConverter : JsonConverter<BlockTransactions>
{
    public override BlockTransactions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray || !reader.Read()) throw new JsonException("Expected an array of transactions");
        if (reader.TokenType == JsonTokenType.EndArray) return BlockTransactions.Empty;

        return reader.TokenType == JsonTokenType.String
            ? ReadAll<Hash256>(ref reader, options, JsonTokenType.String)
            : ReadAll<TransactionForRpc>(ref reader, options, JsonTokenType.StartObject);
    }

    public override void Write(Utf8JsonWriter writer, BlockTransactions value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        if (value.Full is { } full)
        {
            // Through the TransactionForRpc dispatch, which writes each transaction as its runtime type.
            WriteAll(writer, full, options);
        }
        else
        {
            WriteAll(writer, value.Hashes!, options);
        }

        writer.WriteEndArray();
    }

    private static BlockTransactions ReadAll<T>(ref Utf8JsonReader reader, JsonSerializerOptions options, JsonTokenType elementToken) where T : class
    {
        JsonConverter<T> converter = (JsonConverter<T>)TypeInfoJsonSerializer.GetTypeInfo<T>(options).Converter;
        List<T> items = [];
        do
        {
            if (reader.TokenType != elementToken) throw new JsonException("Block transactions mix hashes and transaction objects");
            items.Add(converter.Read(ref reader, typeof(T), options) ?? throw new JsonException("Block transactions contain null"));
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray);

        return items switch
        {
            List<Hash256> hashes => hashes.ToArray(),
            List<TransactionForRpc> transactions => transactions.ToArray(),
            _ => throw new InvalidOperationException(),
        };
    }

    private static void WriteAll<T>(Utf8JsonWriter writer, T[] items, JsonSerializerOptions options) where T : class
    {
        JsonConverter<T> converter = (JsonConverter<T>)TypeInfoJsonSerializer.GetTypeInfo<T>(options).Converter;
        foreach (T item in items)
        {
            if (item is null) writer.WriteNullValue();
            else converter.Write(writer, item, options);
        }
    }
}
