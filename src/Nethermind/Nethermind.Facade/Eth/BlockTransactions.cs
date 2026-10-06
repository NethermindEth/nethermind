// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

    internal static BlockTransactions Empty => EmptyInstance;

    // A null array maps to a null wrapper, so the field is omitted.
    [return: NotNullIfNotNull(nameof(hashes))]
    public static implicit operator BlockTransactions?(Hash256[]? hashes) =>
        hashes is null ? null : hashes.Length == 0 ? EmptyInstance : new(hashes, null);

    [return: NotNullIfNotNull(nameof(transactions))]
    public static implicit operator BlockTransactions?(TransactionForRpc[]? transactions) =>
        transactions is null ? null : transactions.Length == 0 ? EmptyInstance : new(null, transactions);
}

/// <summary>Writes hashes or full transactions as the array the RPC block carries, and reads each form back.</summary>
/// <remarks>Public because source-generated contexts in other assemblies instantiate the converter <see cref="BlockTransactions"/> names.</remarks>
public sealed class BlockTransactionsConverter : JsonConverter<BlockTransactions>
{
    public override BlockTransactions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray || !reader.Read()) throw new JsonException("Expected an array of transactions");
        if (reader.TokenType == JsonTokenType.EndArray) return BlockTransactions.Empty;

        return reader.TokenType == JsonTokenType.String
            ? ReadAll<Hash256>(ref reader, options, JsonTokenType.String)
            : (BlockTransactions)ReadAll<TransactionForRpc>(ref reader, options, JsonTokenType.StartObject);
    }

    public override void Write(Utf8JsonWriter writer, BlockTransactions value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        // The depth is checked before every element, null included, as STJ's array converter does.
        int maxDepth = GeneratedJsonWriters.GetMaxDepth(options);
        if (value.Full is { } full)
        {
            // Written as the runtime type, through its generated writer when it has one.
            foreach (TransactionForRpc transaction in full)
            {
                if (writer.CurrentDepth >= maxDepth) GeneratedJsonWriters.ThrowMaxDepthExceeded(maxDepth);
                if (transaction is null) writer.WriteNullValue();
                else TransactionForRpc.TransactionJsonConverter.WriteAsRuntimeType(writer, transaction, options);
            }
        }
        else
        {
            JsonConverter<Hash256>? converter = TypeInfoJsonSerializer.GetTypeInfo<Hash256>(options).Converter as JsonConverter<Hash256>;
            foreach (Hash256 hash in value.Hashes!)
            {
                if (writer.CurrentDepth >= maxDepth) GeneratedJsonWriters.ThrowMaxDepthExceeded(maxDepth);
                if (hash is null) writer.WriteNullValue();
                else if (converter is not null) converter.Write(writer, hash, options);
                // A converter STJ adapts by casting is only reachable through the serializer.
                else TypeInfoJsonSerializer.Serialize(writer, hash, options);
            }
        }

        writer.WriteEndArray();
    }

    private static T[] ReadAll<T>(ref Utf8JsonReader reader, JsonSerializerOptions options, JsonTokenType elementToken) where T : class
    {
        JsonConverter<T>? converter = TypeInfoJsonSerializer.GetTypeInfo<T>(options).Converter as JsonConverter<T>;
        List<T> items = [];
        do
        {
            if (reader.TokenType != elementToken) ThrowUnexpectedElement(reader.TokenType, elementToken);
            T? item = converter is not null
                ? converter.Read(ref reader, typeof(T), options)
                : TypeInfoJsonSerializer.Deserialize<T>(ref reader, options);
            items.Add(item ?? throw new JsonException("Block transactions contain null"));
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray);

        return items.ToArray();
    }

    [DoesNotReturn]
    private static void ThrowUnexpectedElement(JsonTokenType actual, JsonTokenType expected) =>
        throw new JsonException(actual switch
        {
            JsonTokenType.Null => "Block transactions contain null",
            JsonTokenType.String or JsonTokenType.StartObject => "Block transactions mix hashes and transaction objects",
            _ => $"Unexpected {actual} in block transactions, expected {(expected == JsonTokenType.String ? "hashes" : "transaction objects")}",
        });
}
