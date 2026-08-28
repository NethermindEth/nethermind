// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

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
    public ulong? BlockNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ulong? BlockTimestamp { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ulong? Gas { get; set; }

    // True when type came from a fallback (gasPrice-only or absolute default), not from an
    // explicit `type` field or a discriminator. Set only during JSON deserialization.
    [JsonIgnore]
    internal bool IsTypeDefaulted { get; set; }

    [JsonConstructor]
    protected TransactionForRpc() { }

    protected TransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
    {
        Hash = transaction.Hash;
        TransactionIndex = extraData.TxIndex;
        BlockHash = extraData.BlockHash;
        BlockNumber = extraData.BlockNumber;
        BlockTimestamp = extraData.BlockTimestamp;
    }

    public virtual Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null)
        => new Transaction { Type = ResolveType(spec) };

    private TxType ResolveType(IReleaseSpec? spec)
    {
        // Pre-Berlin only knows Legacy; defaulted-type requests downgrade to avoid EVM rejection.
        TxType type = Type ?? default;
        return spec is not null && !spec.IsEip2930Enabled && IsTypeDefaulted ? TxType.Legacy : type;
    }

    /// <summary>
    /// Validates fields required for signing (gas, fee, nonce), promotes type-defaulted
    /// transactions to EIP-1559, and returns the resulting <see cref="Transaction"/>.
    /// </summary>
    public Result<Transaction> ToSignableTransaction()
    {
        if (Gas is null)
            return Result<Transaction>.Fail("gas not specified");

        if (!HasFeeFields(this))
            return Result<Transaction>.Fail("missing gasPrice or maxFeePerGas/maxPriorityFeePerGas");

        // All concrete tx subtypes (AccessList, EIP1559, Blob, SetCode) derive from LegacyTransactionForRpc.
        if (this is not LegacyTransactionForRpc { Nonce: not null })
            return Result<Transaction>.Fail("nonce not specified");

        return PromoteToEip1559IfTypeDefaulted().ToTransaction(validateUserInput: true);
    }

    private static bool HasFeeFields(TransactionForRpc rpcTx) =>
        rpcTx is EIP1559TransactionForRpc { MaxFeePerGas: not null, MaxPriorityFeePerGas: not null }
            or LegacyTransactionForRpc { GasPrice: not null };

    public TransactionForRpc PromoteToEip1559IfTypeDefaulted()
    {
        if (!IsTypeDefaulted) return this;
        // AccessList and its descendants (EIP1559/Blob/SetCode) are already typed — only plain Legacy promotes.
        if (this is AccessListTransactionForRpc) return this;
        if (this is not LegacyTransactionForRpc legacy) return this;

        return new EIP1559TransactionForRpc
        {
            From = legacy.From,
            To = legacy.To,
            Value = legacy.Value,
            Gas = legacy.Gas,
            Nonce = legacy.Nonce,
            Input = legacy.Input,
            ChainId = legacy.ChainId,
            MaxFeePerGas = legacy.GasPrice,
            MaxPriorityFeePerGas = legacy.GasPrice,
        };
    }

    /// <summary>
    /// Fills the type-specific fields the caller left unset from node-computed defaults: each
    /// transaction type populates the fee model it uses, and blob transactions additionally derive
    /// the KZG sidecar from the supplied blobs.
    /// </summary>
    public virtual Result FillDefaults(in TxFillContext context) => Result.Success;

    public abstract bool ShouldSetBaseFee();

    internal class TransactionJsonConverter : JsonConverter<TransactionForRpc>
    {
        private static readonly List<TxTypeInfo> _txTypes = [];
        private delegate TransactionForRpc FromTransactionFunc(Transaction tx, in TransactionForRpcContext extraData);

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
            RegisterTransactionType<FrameTransactionForRpc>();
        }

        internal static void RegisterTransactionType<T>() where T : TransactionForRpc, IFromTransaction<T>, ITxTyped
        {
            Type txType = typeof(T);
            string[] uniqueProperties = txType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.GetCustomAttribute<JsonDiscriminatorAttribute>() is not null)
                .Select(p => p.Name).ToArray();

            TxTypeInfo typeInfo = new()
            {
                TxType = T.TxType,
                Type = txType,
                FromTransactionFunc = T.FromTransaction,
                DiscriminatorPropertiesUtf8 = Array.ConvertAll(uniqueProperties, static p => Encoding.UTF8.GetBytes(p.ToLowerInvariant()))
            };

            int existingTypeInfo = _txTypes.FindIndex(t => t.TxType == typeInfo.TxType);

            if (existingTypeInfo != -1)
            {
                _txTypes[existingTypeInfo] = typeInfo;
            }
            else
            {
                // Discriminator bitset in DeriveTxType is ulong — keep registration count within it.
                Debug.Assert(_txTypes.Count < 64);

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
            // Peek property names for the concrete type, then deserialize (no DOM).
            Utf8JsonReader txTypeReader = reader;

            Type concreteTxType = DeriveTxType(ref txTypeReader, options, out bool isDefaulted);

            TransactionForRpc? result = (TransactionForRpc?)JsonSerializer.Deserialize(ref reader, concreteTxType, options);
            if (result is not null)
            {
                result.IsTypeDefaulted = isDefaulted;
            }
            return result;
        }

        private static ReadOnlySpan<byte> TypeFieldUtf8 => "type"u8;
        private static ReadOnlySpan<byte> GasPriceFieldUtf8 => "gasprice"u8;

        private Type DeriveTxType(ref Utf8JsonReader reader, JsonSerializerOptions options, out bool isDefaulted)
        {
            TxType? setType = null;
            bool hasGasPrice = false;
            // Bit i set ⇒ discriminator for _txTypes[i] seen; lowest bit wins (registration order).
            ulong discriminated = 0;

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (setType is null && NameEqualsIgnoreCase(ref reader, TypeFieldUtf8))
                    {
                        reader.Read();
                        setType = JsonSerializer.Deserialize<TxType?>(ref reader, options);
                        // Explicit type fully determines the concrete class — stop scanning large payloads.
                        if (setType is not null) break;
                        continue;
                    }

                    if (!hasGasPrice && NameEqualsIgnoreCase(ref reader, GasPriceFieldUtf8))
                    {
                        hasGasPrice = true;
                    }
                    else
                    {
                        int count = _txTypes.Count;
                        for (int i = 0; i < count; i++)
                        {
                            foreach (byte[] discriminator in _txTypes[i].DiscriminatorPropertiesUtf8)
                            {
                                if (NameEqualsIgnoreCase(ref reader, discriminator))
                                {
                                    discriminated |= 1UL << i;
                                    break;
                                }
                            }
                        }
                    }

                    reader.Read();
                    if (!reader.TrySkip()) break;
                }
            }

            Type? viaDiscriminator = null;
            if (discriminated != 0)
            {
                viaDiscriminator = _txTypes[BitOperations.TrailingZeroCount(discriminated)].Type;
            }

            if (setType is not null)
            {
                isDefaulted = false;
                foreach (TxTypeInfo candidate in _txTypes)
                {
                    if (candidate.TxType == setType) return candidate.Type;
                }

                throw new JsonException("Unknown transaction type");
            }

            if (hasGasPrice)
            {
                isDefaulted = true;
                return typeof(LegacyTransactionForRpc);
            }

            // Discriminator field is a strong signal — not a default.
            if (viaDiscriminator is not null)
            {
                isDefaulted = false;
                return viaDiscriminator;
            }

            isDefaulted = true;
            return typeof(EIP1559TransactionForRpc);
        }

        // lowerCaseName must be pure ASCII letters — |0x20 fold is only sound for that alphabet.
        private static bool NameEqualsIgnoreCase(ref Utf8JsonReader reader, ReadOnlySpan<byte> lowerCaseName)
        {
            if (!reader.HasValueSequence && !reader.ValueIsEscaped)
            {
                ReadOnlySpan<byte> name = reader.ValueSpan;
                if (name.Length != lowerCaseName.Length) return false;
                for (int i = 0; i < name.Length; i++)
                {
                    if ((name[i] | 0x20) != lowerCaseName[i]) return false;
                }

                return true;
            }

            // Escaped / multi-segment: ordinal match first (no alloc), then one unescaped compare.
            if (reader.ValueTextEquals(lowerCaseName)) return true;

            string? unescaped = reader.GetString();
            if (unescaped is null || unescaped.Length != lowerCaseName.Length) return false;
            for (int i = 0; i < unescaped.Length; i++)
            {
                char c = unescaped[i];
                if (c > 0x7f || ((byte)c | 0x20) != lowerCaseName[i]) return false;
            }

            return true;
        }

        public override void Write(Utf8JsonWriter writer, TransactionForRpc value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value, value.GetType(), options);

        public static TransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData) => _txTypes.FirstOrDefault(t => t.TxType == tx.Type)?.FromTransactionFunc(tx, extraData)
                ?? throw new ArgumentException("No converter for transaction type");

        class TxTypeInfo
        {
            public TxType TxType { get; set; }
            public Type Type { get; set; }
            public FromTransactionFunc FromTransactionFunc { get; set; }
            public byte[][] DiscriminatorPropertiesUtf8 { get; set; } = [];
        }
    }

    public static TransactionForRpc FromTransaction(Transaction transaction, in TransactionForRpcContext? extraData = null) =>
        TransactionJsonConverter.FromTransaction(transaction, extraData ?? default);

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
