// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>
/// Base class for all output Nethermind RPC Transactions.
/// </summary>
/// <remarks>
/// Input:
/// <para>JSON -> <see cref="TransactionForRpc"></see> (through <see cref="JsonConverter"/>, a registry of [<see cref="TxType"/> => <see cref="TransactionForRpc"/> subtypes)</para>
/// <para><see cref="TransactionForRpc"/> -> <see cref="Transaction"/> (through an overloaded <see cref="ToTransaction"> method)</para>
/// Output:
/// <para><see cref="Transaction"/> -> <see cref="TransactionForRpc"/> (through <see cref="TransactionConverter"/>, a registry of [<see cref="TxType"/> => <see cref="IFromTransaction{T}"/>)</para>
/// <para><see cref="TransactionForRpc"/> -> JSON (Derived by <c>System.Text.JSON</c> using the runtime type)</para>
/// </remarks>
[JsonConverter(typeof(JsonConverter))]
public abstract class TransactionForRpc
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public virtual TxType? Type => null;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256? Hash { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? TransactionIndex { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256? BlockHash { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? BlockNumber { get; set; }

    [JsonConstructor]
    protected TransactionForRpc() { }

    protected TransactionForRpc(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null)
    {
        Hash = transaction.Hash;
        TransactionIndex = txIndex;
        BlockHash = blockHash;
        BlockNumber = blockNumber;
    }

    public virtual Transaction ToTransaction()
    {
        return new Transaction
        {
            Type = Type ?? default,
        };
    }

    public abstract void EnsureDefaults(long? gasCap);

    public class JsonConverter : JsonConverter<TransactionForRpc>
    {
        private const TxType DefaultTxType = TxType.Legacy;
        private static readonly Type[] _transactionTypes = new Type[Transaction.MaxTxType + 1];

        // TODO: Refactoring transition code
        public JsonConverter()
        {
            RegisterTransactionType<LegacyTransactionForRpc>();
            RegisterTransactionType<AccessListTransactionForRpc>();
            RegisterTransactionType<EIP1559TransactionForRpc>();
            RegisterTransactionType<BlobTransactionForRpc>();
        }

        public static void RegisterTransactionType<T>() where T : TransactionForRpc, ITxTyped => _transactionTypes[(int)T.TxType] = typeof(T);

        public override TransactionForRpc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);

            // TODO: For some reason we might get an object wrapped in a String
            using JsonDocument jsonObject = document.RootElement.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(document.RootElement.GetString()!)
                : document;

            // NOTE: Should we default to a specific TxType?
            TxType discriminator = DefaultTxType;
            if (jsonObject.RootElement.TryGetProperty("type", out JsonElement typeProperty))
            {
                discriminator = (TxType?)typeProperty.Deserialize(typeof(TxType), options) ?? DefaultTxType;
            }

            return _transactionTypes.TryGetByTxType(discriminator, out Type concreteTxType)
                ? (TransactionForRpc?)jsonObject.Deserialize(concreteTxType, options)
                : throw new JsonException("Unknown transaction type");
        }

        public override void Write(Utf8JsonWriter writer, TransactionForRpc value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
    }

    public class TransactionConverter : IFromTransaction<TransactionForRpc>
    {
        private readonly IFromTransaction<TransactionForRpc>?[] _converters = new IFromTransaction<TransactionForRpc>?[Transaction.MaxTxType + 1];

        public TransactionConverter RegisterConverter<T>() where T : TransactionForRpc, IFromTransactionSource<T>
        {
            _converters[(byte)T.TxType] = T.Converter;
            return this;
        }

        public TransactionForRpc FromTransaction(Transaction tx, TransactionConverterExtraData extraData) =>
            _converters.TryGetByTxType(tx.Type, out IFromTransaction<TransactionForRpc> converter)
                ? converter.FromTransaction(tx, extraData)
                : throw new ArgumentException("No converter for transaction type");
    }

    #region Refactoring transition code
    public static readonly TransactionConverter GlobalConverter = new TransactionConverter()
        .RegisterConverter<LegacyTransactionForRpc>()
        .RegisterConverter<AccessListTransactionForRpc>()
        .RegisterConverter<EIP1559TransactionForRpc>()
        .RegisterConverter<BlobTransactionForRpc>();

    public static TransactionForRpc FromTransaction(Transaction transaction, Hash256? blockHash = null, long? blockNumber = null, int? txIndex = null, UInt256? baseFee = null) =>
        GlobalConverter.FromTransaction(transaction, new()
        {
            TxIndex = txIndex,
            BlockHash = blockHash,
            BlockNumber = blockNumber,
            BaseFee = baseFee
        });

    #endregion
}
