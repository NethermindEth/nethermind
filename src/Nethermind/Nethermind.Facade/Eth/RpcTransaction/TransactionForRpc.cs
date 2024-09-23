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
    public TransactionForRpc() { }

    public TransactionForRpc(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null)
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
        private readonly Type[] _transactionTypes = new Type[Transaction.MaxTxType + 1];

        // TODO: Refactoring transition code
        public JsonConverter()
        {
            RegisterTransactionType(TxType.Legacy, typeof(LegacyTransactionForRpc));
            RegisterTransactionType(TxType.AccessList, typeof(AccessListTransactionForRpc));
            RegisterTransactionType(TxType.EIP1559, typeof(EIP1559TransactionForRpc));
            RegisterTransactionType(TxType.Blob, typeof(BlobTransactionForRpc));
            // TODO: Add Optimism:
            // RegisterTransactionType(TxType.DepositTx, typeof(RpcOptimismTransaction))
        }

        public JsonConverter RegisterTransactionType(TxType type, Type @class)
        {
            if (!@class.IsSubclassOf(typeof(TransactionForRpc)))
            {
                throw new ArgumentException($"{@class.FullName} is not a subclass of ${nameof(TransactionForRpc)}");
            }

            _transactionTypes[(byte)type] = @class;
            return this;
        }

        public override TransactionForRpc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);

            // TODO: For some reason we might get an object wrapped in a String
            using JsonDocument jsonObject = document.RootElement.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(document.RootElement.GetString()!)
                : document;

            // NOTE: Should we default to a specific TxType?
            TxType discriminator = default;
            if (jsonObject.RootElement.TryGetProperty("type", out JsonElement typeProperty))
            {
                discriminator = (TxType?)typeProperty.Deserialize(typeof(TxType), options) ?? default;
            }

            Type concreteTxType = _transactionTypes[(byte)discriminator];

            return (TransactionForRpc?)jsonObject.Deserialize(concreteTxType, options);
        }

        public override void Write(Utf8JsonWriter writer, TransactionForRpc value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
    }

    public class TransactionConverter : IFromTransaction<TransactionForRpc>
    {
        private readonly IFromTransaction<TransactionForRpc>?[] _converters = new IFromTransaction<TransactionForRpc>?[Transaction.MaxTxType + 1];

        public TransactionConverter RegisterConverter(TxType txType, IFromTransaction<TransactionForRpc> converter)
        {
            _converters[(byte)txType] = converter;
            return this;
        }

        public TransactionForRpc FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
        {
            var converter = _converters[(byte)tx.Type] ?? throw new ArgumentException("No converter for transaction type");
            return converter.FromTransaction(tx, extraData);
        }
    }

    #region Refactoring transition code
    public static readonly TransactionConverter GlobalConverter = new TransactionConverter()
        .RegisterConverter(TxType.Legacy, LegacyTransactionForRpc.Converter)
        .RegisterConverter(TxType.AccessList, AccessListTransactionForRpc.Converter)
        .RegisterConverter(TxType.EIP1559, EIP1559TransactionForRpc.Converter)
        .RegisterConverter(TxType.Blob, BlobTransactionForRpc.Converter);
    // TODO: Add Optimism:
    // `.RegisterConverter(TxType.DepositTx, RpcOptimismTransaction.Converter)`

    public static TransactionForRpc FromTransaction(Transaction transaction, Hash256? blockHash = null, long? blockNumber = null, int? txIndex = null, UInt256? baseFee = null)
    {
        var extraData = new TransactionConverterExtraData
        {
            TxIndex = txIndex,
            BlockHash = blockHash,
            BlockNumber = blockNumber,
            BaseFee = baseFee
        };
        return GlobalConverter.FromTransaction(transaction, extraData);
    }
    #endregion
}
