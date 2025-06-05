// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <para>JSON -> <see cref="TransactionForRpc"></see> (through <see cref="TransactionJsonConverter"/>, a registry of [<see cref="TxType"/> => <see cref="TransactionForRpc"/> subtypes)</para>
/// <para><see cref="TransactionForRpc"/> -> <see cref="Transaction"/> (through an overloaded <see cref="ToTransaction">method</see>)</para>
/// Output:
/// <para><see cref="Transaction"/> -> <see cref="TransactionForRpc"/> (through <see cref="TransactionJsonConverter"/>, a registry of [<see cref="TxType"/> => <see cref="IFromTransaction{T}"/>)</para>
/// <para><see cref="TransactionForRpc"/> -> JSON (Derived by <c>System.Text.JSON</c> using the runtime type)</para>
/// </remarks>
[JsonConverter(typeof(TransactionJsonConverter))]
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

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? Gas { get; set; }

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
    public abstract bool ShouldSetBaseFee();

    internal class TransactionJsonConverter : JsonConverter<TransactionForRpc>
    {
        private static readonly List<TxTypeInfo> _txTypes = [];
        private delegate TransactionForRpc FromTransactionFunc(Transaction tx, TransactionConverterExtraData extraData);

        /// <summary>
        /// Transaction type is determined based on type field or type-specific fields present in the request
        /// </summary>
        static TransactionJsonConverter()
        {
            RegisterTransactionType<LegacyTransactionForRpc>();
            RegisterTransactionType<AccessListTransactionForRpc>();
            RegisterTransactionType<EIP1559TransactionForRpc>();
            RegisterTransactionType<BlobTransactionForRpc>();
            RegisterTransactionType<SetCodeTransactionForRpc>();
        }

        internal static void RegisterTransactionType<T>() where T : TransactionForRpc, IFromTransaction<T>, ITxTyped
        {
            Type txType = typeof(T);
            string[] uniqueProperties = txType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.GetCustomAttribute<JsonDiscriminatorAttribute>() is not null)
                .Select(p => p.Name).ToArray();

            TxTypeInfo typeInfo = new TxTypeInfo
            {
                TxType = T.TxType,
                Type = txType,
                FromTransactionFunc = T.FromTransaction,
                DiscriminatorProperties = uniqueProperties
            };

            int existingTypeInfo = _txTypes.FindIndex(t => t.TxType == typeInfo.TxType);

            if (existingTypeInfo != -1)
            {
                _txTypes[existingTypeInfo] = typeInfo;
            }
            else
            {
                // Adding in reverse order so newer tx types are in priority
                int indexOfPreviousTxType = _txTypes.FindIndex(t => t.TxType < typeInfo.TxType);
                if (indexOfPreviousTxType != -1)
                {
                    _txTypes.Insert(indexOfPreviousTxType, typeInfo);
                }
                else
                {
                    _txTypes.Add(typeInfo);
                }
            }
        }

        public override TransactionForRpc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            const string TypeFieldKey = nameof(TransactionForRpc.Type);
            // Copy the reader so we can do a double parse:
            // The first parse is used to check for fields, while the second parses the entire Transaction
            Utf8JsonReader txTypeReader = reader;
            JsonObject untyped = JsonSerializer.Deserialize<JsonObject>(ref txTypeReader, options);

            TxType? txType = null;
            if (untyped.TryGetPropertyValue(TypeFieldKey, out JsonNode? node))
            {
                txType = node.Deserialize<TxType?>(options);
            }

            Type concreteTxType =
                (
                    txType is not null
                        ? _txTypes.FirstOrDefault(p => p.TxType == txType)
                        : _txTypes.FirstOrDefault(p => p.DiscriminatorProperties.Any(name => untyped.ContainsKey(name)), _txTypes[^1])
                )?.Type
                ?? throw new JsonException("Unknown transaction type");

            return (TransactionForRpc?)JsonSerializer.Deserialize(ref reader, concreteTxType, options);
        }

        public override void Write(Utf8JsonWriter writer, TransactionForRpc value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }

        public static TransactionForRpc FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
        {
            return _txTypes.FirstOrDefault(t => t.TxType == tx.Type)?.FromTransactionFunc(tx, extraData)
                ?? throw new ArgumentException("No converter for transaction type");
        }

        class TxTypeInfo
        {
            public TxType TxType { get; set; }
            public Type Type { get; set; }
            public FromTransactionFunc FromTransactionFunc { get; set; }
            public string[] DiscriminatorProperties { get; set; } = [];
        }
    }

    public static TransactionForRpc FromTransaction(Transaction transaction, Hash256? blockHash = null, long? blockNumber = null, int? txIndex = null, UInt256? baseFee = null, ulong? chainId = null) =>
        TransactionJsonConverter.FromTransaction(transaction, new()
        {
            ChainId = chainId,
            TxIndex = txIndex,
            BlockHash = blockHash,
            BlockNumber = blockNumber,
            BaseFee = baseFee
        });

    public static void RegisterTransactionType<T>() where T : TransactionForRpc, IFromTransaction<T>, ITxTyped => TransactionJsonConverter.RegisterTransactionType<T>();
}

/// <summary>
/// Marks fields that determine the transaction type
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class JsonDiscriminatorAttribute : Attribute
{
    public JsonDiscriminatorAttribute() { }
}
