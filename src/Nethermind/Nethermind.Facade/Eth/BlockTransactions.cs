// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth;

/// <summary>The <c>transactions</c> of an RPC block: either their hashes or the full transaction objects.</summary>
/// <remarks>
/// <para>
/// A class rather than a struct so that a block built without transactions keeps omitting the field, as a null array did.
/// An empty array read from JSON says neither which form it was, so <see cref="Hashes"/> and <see cref="Full"/> are both empty.
/// </para>
/// <para>
/// Built from a block, the full form is a view over the block's stored transactions: <see cref="BlockTransactionsConverter"/>
/// writes them one at a time through one reused RPC object per transaction type, and <see cref="Full"/> builds new objects on
/// each access. The block's coordinates are taken when the view is created.
/// </para>
/// </remarks>
[JsonConverter(typeof(BlockTransactionsConverter))]
public sealed class BlockTransactions
{
    private static readonly BlockTransactions EmptyInstance = new([], []);

    private readonly TransactionForRpc[]? _full;
    private readonly Transaction[]? _source;
    private readonly ulong _chainId;
    private readonly Hash256? _blockHash;
    private readonly ulong _blockNumber;
    private readonly ulong _blockTimestamp;
    private readonly UInt256 _baseFee;

    private BlockTransactions(Hash256[]? hashes, TransactionForRpc[]? full)
    {
        Hashes = hashes;
        _full = full;
    }

    private BlockTransactions(Block block, ulong chainId)
    {
        _source = block.Transactions;
        _chainId = chainId;
        _blockHash = block.Hash;
        _blockNumber = block.Number;
        _blockTimestamp = block.Timestamp;
        _baseFee = block.BaseFeePerGas;
    }

    /// <summary>The transaction hashes, or <see langword="null"/> for a block carrying full transactions.</summary>
    public Hash256[]? Hashes { get; }

    /// <summary>The full transactions, or <see langword="null"/> for a block carrying hashes.</summary>
    /// <remarks>For a view over a block, each access builds a new array of new objects, so a change to one is not kept.</remarks>
    public TransactionForRpc[]? Full => _full ?? (_source is null ? null : Materialize());

    internal static BlockTransactions Empty => EmptyInstance;

    /// <summary>Creates the full form as a view over <paramref name="block"/>'s transactions.</summary>
    /// <exception cref="ArgumentException">A transaction has a type with no registered RPC type, as building it eagerly would report.</exception>
    internal static BlockTransactions FromBlock(Block block, ulong chainId)
    {
        Transaction[] transactions = block.Transactions;
        if (transactions.Length == 0) return EmptyInstance;

        // Fail while the response is built, not part-way through writing it.
        foreach (Transaction transaction in transactions)
        {
            if (TransactionForRpc.TransactionJsonConverter.GetRegisteredType(transaction.Type) is null) throw new ArgumentException("No converter for transaction type");
        }

        return new BlockTransactions(block, chainId);
    }

    /// <summary>Whether this is a view over a block's transactions rather than an array.</summary>
    internal bool IsView => _source is not null;

    internal int ViewCount => _source!.Length;

    internal TxType GetSourceType(int index) => _source![index].Type;

    /// <summary>Builds the RPC object for transaction <paramref name="index"/> of the view.</summary>
    /// <param name="reuse">An instance of the right type to refill instead of creating one, or <see langword="null"/>.</param>
    internal TransactionForRpc GetViewTransaction(int index, TransactionForRpc? reuse)
    {
        TransactionForRpcContext context = new(_chainId, _blockHash!, _blockNumber, index, _blockTimestamp, _baseFee);
        Transaction transaction = _source![index];
        if (reuse is null) return TransactionForRpc.FromTransaction(transaction, context);

        reuse.Populate(transaction, context);
        return reuse;
    }

    private TransactionForRpc[] Materialize()
    {
        TransactionForRpc[] transactions = new TransactionForRpc[_source!.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = GetViewTransaction(i, reuse: null);
        }

        return transactions;
    }

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
    private readonly GeneratedJsonDispatch _generated = new();

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
        if (value.IsView)
        {
            WriteView(writer, value, maxDepth, options);
        }
        else if (value.Full is { } full)
        {
            // Written as the runtime type, through its generated writer when it has one.
            foreach (TransactionForRpc transaction in full)
            {
                if (writer.CurrentDepth >= maxDepth) GeneratedJsonWriters.ThrowMaxDepthExceeded(maxDepth);
                if (transaction is null) writer.WriteNullValue();
                else TransactionForRpc.TransactionJsonConverter.WriteAsRuntimeType(_generated, writer, transaction, options);
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

    // One instance per repopulatable type, local to this call, so concurrent writes never share one.
    private void WriteView(Utf8JsonWriter writer, BlockTransactions value, int maxDepth, JsonSerializerOptions options)
    {
        TransactionForRpc?[] reused = new TransactionForRpc?[TransactionForRpc.TransactionJsonConverter.RepopulatableSlotCount];
        int count = value.ViewCount;
        for (int i = 0; i < count; i++)
        {
            if (writer.CurrentDepth >= maxDepth) GeneratedJsonWriters.ThrowMaxDepthExceeded(maxDepth);

            TransactionForRpc transaction = value.GetViewTransaction(i, TakeReusable(reused, value, i, out int slot));
            if (slot >= 0) reused[slot] = transaction;
            TransactionForRpc.TransactionJsonConverter.WriteAsRuntimeType(_generated, writer, transaction, options);
        }
    }

    private static TransactionForRpc? TakeReusable(TransactionForRpc?[] reused, BlockTransactions value, int index, out int slot)
    {
        Type? type = TransactionForRpc.TransactionJsonConverter.GetRegisteredType(value.GetSourceType(index));
        slot = type is null ? -1 : TransactionForRpc.TransactionJsonConverter.GetRepopulatableSlot(type);
        return slot >= 0 ? reused[slot] : null;
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
