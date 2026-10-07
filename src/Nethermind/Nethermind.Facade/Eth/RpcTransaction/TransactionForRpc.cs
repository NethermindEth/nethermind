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
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
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

    // The explicit `type` the request named, if any. Set only during JSON deserialization.
    [JsonIgnore]
    internal TxType? RequestedType { get; set; }

    /// <summary>
    /// This request as the explicit type it named, for the methods that build or sign a transaction. For the
    /// Ethereum types the fields pick the class during deserialization, so a call never takes a requirement
    /// from its type. A signed transaction keeps the requested type instead. It applies when the fields name
    /// no type of their own (a defaulted class) or when its class derives from the fields' class, so it carries
    /// every field they do; any other requested type conflicts with the fields.
    /// </summary>
    public Result<TransactionForRpc> WithRequestedType()
    {
        if (RequestedType is not { } requested || (requested == Type && !IsTypeDefaulted)) return this;

        // The copy is never defaulted, so the requested type survives later defaulting by spec.
        Type? requestedClass = TransactionJsonConverter.ClassOf(requested);
        if (requestedClass is null || !(IsTypeDefaulted || GetType().IsAssignableFrom(requestedClass)))
            return Result<TransactionForRpc>.Fail($"type {(byte)requested} conflicts with the fields present, which need type {(byte?)Type}");

        TransactionForRpc promoted = (TransactionForRpc)Activator.CreateInstance(requestedClass)!;
        PropertyInfo[] targets = requestedClass.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (PropertyInfo property in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (PropertyInfo target in targets)
            {
                if (target.Name == property.Name && property.GetGetMethod() is not null && target.GetSetMethod() is not null
                    && target.PropertyType.IsAssignableFrom(property.PropertyType))
                {
                    target.SetValue(promoted, property.GetValue(this));
                    break;
                }
            }
        }

        promoted.RequestedType = requested;
        return promoted;
    }

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

    /// <summary>
    /// Converts the request with its input validated, rejecting a fee cap below the priority fee as well; a call that
    /// leaves that pair to execution, where it fails before any gas is bought, validates with <see cref="ToTransaction"/>.
    /// </summary>
    /// <remarks>
    /// The pair is checked after the type-specific and gas price checks and before the missing contract data check,
    /// so the first failing check still names the request.
    /// </remarks>
    public Result<Transaction> ToValidatedTransaction(ulong? gasCap = null, IReleaseSpec? spec = null)
        => CheckFeeCapOrder(ToTransaction(validateUserInput: true, gasCap, spec));

    private Result<Transaction> CheckFeeCapOrder(Result<Transaction> result) =>
        this is EIP1559TransactionForRpc { MaxFeePerGas: { } maxFeePerGas, MaxPriorityFeePerGas: { } maxPriorityFeePerGas }
        && maxFeePerGas < maxPriorityFeePerGas
        && (!result.IsError || result.Error == RpcTransactionErrors.ContractCreationWithoutData)
            ? RpcTransactionErrors.MaxFeePerGasSmallerThanMaxPriorityFeePerGas(maxFeePerGas, maxPriorityFeePerGas)
            : result;

    /// <summary>
    /// Converts an unsigned call that runs in a block of <paramref name="spec"/>, as <see cref="ToValidatedTransaction"/>
    /// does, or as <see cref="ToTransaction"/> does without <paramref name="validateFeeCapOrder"/>. The call's fields
    /// chose its type, so a field that fork lacks names a type it doesn't enable, even when its value is zero or empty,
    /// and a call that converts is rejected before its fee cap order is checked; a defaulted type names no field. Transactions to
    /// build or sign don't run in a known block, so they stay outside this check: they convert with
    /// <see cref="ToValidatedTransaction"/>, or without <paramref name="checksFork"/> when they run as a call to
    /// estimate their gas.
    /// </summary>
    public Result<Transaction> ToCallTransaction(IReleaseSpec spec, ulong? gasCap = null, bool validateUserInput = true, bool validateFeeCapOrder = true, bool checksFork = true)
    {
        Result<Transaction> result = ToTransaction(validateUserInput, gasCap, spec);
        if (checksFork && result.Success(out Transaction? tx, out _) && !IsTypeDefaulted && !spec.IsTxTypeEnabled(tx.Type))
            return TxErrorMessages.InvalidTxType(spec.Name);
        return validateFeeCapOrder ? CheckFeeCapOrder(result) : result;
    }

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

        return PromoteToEip1559IfTypeDefaulted().ToValidatedTransaction();
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
        private static readonly TxTypeInfo?[] _txTypesByType = new TxTypeInfo?[byte.MaxValue + 1];
        private static Registry _registry = new([], [], [], [], []);

        // An immutable view of the registered types and their discriminator fields. It is published whole, so
        // a read that races a registration never mixes the indices of two generations.
        private sealed class Registry(TxTypeInfo[] types, string[] names, byte[][] namesUtf8, ulong[] typesByName, ulong[] namesByType)
        {
            // Newest type first, so the lowest bit of a bitset over Types is the newest type.
            public TxTypeInfo[] Types { get; } = types;
            // Every discriminator field name across the types.
            public string[] Names { get; } = names;
            public byte[][] NamesUtf8 { get; } = namesUtf8;
            // Per name, the bitset of Types it indicates.
            public ulong[] TypesByName { get; } = typesByName;
            // Per type, the bitset of Names it has a property for.
            public ulong[] NamesByType { get; } = namesByType;
        }
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
            lock (_txTypes)
            {
                Register<T>();
                Volatile.Write(ref _registry, BuildRegistry());
            }
        }

        private static void Register<T>() where T : TransactionForRpc, IFromTransaction<T>, ITxTyped
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
                DiscriminatorProperties = uniqueProperties
            };

            _txTypesByType[(byte)typeInfo.TxType] = typeInfo;
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

        internal static Type? ClassOf(TxType type)
        {
            foreach (TxTypeInfo typeInfo in Volatile.Read(ref _registry).Types)
            {
                if (typeInfo.TxType == type) return typeInfo.Type;
            }

            return null;
        }

        // Registration reorders the types and can add field names, so the registry is rebuilt each time.
        private static Registry BuildRegistry()
        {
            TxTypeInfo[] types = [.. _txTypes];
            string[] names = types.SelectMany(t => t.DiscriminatorProperties).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Bitsets over the names in DeriveTxType are ulong too.
            Debug.Assert(names.Length <= 64);

            ulong[] typesByName = new ulong[names.Length];
            ulong[] namesByType = new ulong[types.Length];
            for (int n = 0; n < names.Length; n++)
            {
                for (int i = 0; i < types.Length; i++)
                {
                    if (types[i].DiscriminatorProperties.Contains(names[n], StringComparer.OrdinalIgnoreCase))
                        typesByName[n] |= 1UL << i;
                    if (types[i].Type.GetProperty(names[n], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not null)
                        namesByType[i] |= 1UL << n;
                }
            }

            return new Registry(types, names, Array.ConvertAll(names, static p => Encoding.UTF8.GetBytes(p.ToLowerInvariant())), typesByName, namesByType);
        }

        public override TransactionForRpc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Peek property names for the concrete type, then deserialize (no DOM).
            Utf8JsonReader txTypeReader = reader;

            Type concreteTxType = DeriveTxType(ref txTypeReader, options, out bool isDefaulted, out TxType? requestedType);

            TransactionForRpc? result = (TransactionForRpc?)JsonSerializer.Deserialize(ref reader, concreteTxType, options);
            if (result is not null)
            {
                result.IsTypeDefaulted = isDefaulted;
                result.RequestedType = requestedType;
            }
            return result;
        }

        private static ReadOnlySpan<byte> TypeFieldUtf8 => "type"u8;
        private static ReadOnlySpan<byte> GasPriceFieldUtf8 => "gasprice"u8;

        private Type DeriveTxType(ref Utf8JsonReader reader, JsonSerializerOptions options, out bool isDefaulted, out TxType? requestedType)
        {
            Registry registry = Volatile.Read(ref _registry);
            TxType? setType = null;
            bool hasGasPrice = false;
            // Bit i set ⇒ non-null discriminator for registry.Types[i] seen; lowest bit wins (registration order).
            // An explicit null is the same as omitting the member, as in geth, which keys on non-nil fields.
            ulong discriminated = 0;
            // Bit n set ⇒ non-null registry.Names[n] seen.
            ulong seen = 0;

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (setType is null && NameEqualsIgnoreCase(ref reader, TypeFieldUtf8))
                    {
                        reader.Read();
                        setType = JsonSerializer.Deserialize<TxType?>(ref reader, options);
                        continue;
                    }

                    int name = -1;
                    bool isGasPrice = false;
                    if (!hasGasPrice && NameEqualsIgnoreCase(ref reader, GasPriceFieldUtf8))
                    {
                        isGasPrice = true;
                    }
                    else
                    {
                        byte[][] names = registry.NamesUtf8;
                        for (int n = 0; n < names.Length; n++)
                        {
                            if (NameEqualsIgnoreCase(ref reader, names[n]))
                            {
                                name = n;
                                break;
                            }
                        }
                    }

                    reader.Read();
                    if (reader.TokenType != JsonTokenType.Null)
                    {
                        if (name != -1)
                        {
                            seen |= 1UL << name;
                            discriminated |= registry.TypesByName[name];
                        }
                        hasGasPrice |= isGasPrice;
                    }

                    if (!reader.TrySkip()) break;
                }
            }

            requestedType = setType;

            if (setType is not null)
            {
                int index = -1;
                for (int i = 0; i < registry.Types.Length; i++)
                {
                    if (registry.Types[i].TxType == setType)
                    {
                        index = i;
                        break;
                    }
                }

                if (index == -1) throw new JsonException("Unknown transaction type");

                // For the Ethereum types up to set-code the fields pick the class, so a call neither drops a field
                // nor takes a requirement from its explicit type; the signing methods apply it afterwards
                // (WithRequestedType). Any other type, such as a chain extension, picks its class as before.
                if (setType > TxType.SetCode)
                    discriminated |= 1UL << index;
            }

            // Discriminator field is a strong signal — not a default. It wins over gasPrice, otherwise a
            // legacy-priced request would silently lose its accessList/blobVersionedHashes/authorizationList.
            // The newest indicated type must have every discriminator field seen; fields no single type
            // carries, such as blobVersionedHashes with authorizationList, are rejected rather than dropped.
            if (discriminated != 0)
            {
                int index = BitOperations.TrailingZeroCount(discriminated);
                TxTypeInfo selected = registry.Types[index];
                ulong missing = seen & ~registry.NamesByType[index];
                if (missing != 0)
                    throw new JsonException($"{registry.Names[BitOperations.TrailingZeroCount(missing)]} is not a field of transaction type {(byte)selected.TxType}");

                isDefaulted = false;
                return selected.Type;
            }

            if (hasGasPrice)
            {
                isDefaulted = true;
                return typeof(LegacyTransactionForRpc);
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

        public static TransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData) => _txTypesByType[(byte)tx.Type]?.FromTransactionFunc(tx, extraData)
                ?? throw new ArgumentException("No converter for transaction type");

        class TxTypeInfo
        {
            public TxType TxType { get; set; }
            public Type Type { get; set; }
            public FromTransactionFunc FromTransactionFunc { get; set; }
            public string[] DiscriminatorProperties { get; set; } = [];
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
