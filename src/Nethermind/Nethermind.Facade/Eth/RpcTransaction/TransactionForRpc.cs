// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Json;

[assembly: InternalsVisibleTo("Nethermind.JsonRpc")]

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

    // True when type came from a fallback (gasPrice-only or absolute default), not from a discriminator
    // or a registered type above set-code. Set only during JSON deserialization.
    [JsonIgnore]
    internal bool IsTypeDefaulted { get; set; }

    [JsonConstructor]
    protected TransactionForRpc() { }

    // ReSharper disable once VirtualMemberCallInConstructor
    protected TransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData) => Populate(transaction, extraData);

    /// <summary>Sets every property from <paramref name="transaction"/>, so one instance can be reused for each transaction of a block.</summary>
    /// <remarks>
    /// The constructor runs it, so construction and reuse share one implementation. Overrides call the base first and then
    /// assign every property they declare, including back to null or default, because a reused instance carries the previous
    /// transaction's values. Types in other assemblies cannot override it and are never reused.
    /// </remarks>
    internal virtual void Populate(Transaction transaction, in TransactionForRpcContext extraData)
    {
        Hash = transaction.Hash;
        TransactionIndex = extraData.TxIndex;
        BlockHash = extraData.BlockHash;
        BlockNumber = extraData.BlockNumber;
        BlockTimestamp = extraData.BlockTimestamp;
        Gas = null;
        IsTypeDefaulted = false;
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

    /// <summary>Converts an unsigned call that runs in a block of <paramref name="spec"/>, rejecting a field that block's fork lacks.</summary>
    /// <param name="spec">The fork of the block the call runs in, or null when the block is unknown and the fork isn't checked.</param>
    /// <param name="gasCap">The gas limit applied when the call sets none or a higher one.</param>
    /// <param name="validateUserInput">Whether the request's fields are validated, as in <see cref="ToTransaction"/>.</param>
    /// <param name="validateFeeCapOrder">Whether a fee cap below the priority fee is rejected, as in <see cref="ToValidatedTransaction"/>.</param>
    /// <param name="checksFork">Whether the fork is checked; off for a transaction to build that runs as a call to estimate its gas.</param>
    /// <remarks>
    /// The call's fields chose its type, so a field the fork lacks names a type it doesn't enable, even when its value is
    /// zero or empty; a defaulted type names no field. A fee cap below the priority fee is wrong in any block, so it is
    /// reported in place of a failed fork check, as trace-interop H14 asks, even when otherwise left to execution.
    /// Transactions to build or sign go into a later block, so they stay outside this check and convert with
    /// <see cref="ToValidatedTransaction"/>.
    /// </remarks>
    public Result<Transaction> ToCallTransaction(IReleaseSpec? spec, ulong? gasCap = null, bool validateUserInput = true, bool validateFeeCapOrder = true, bool checksFork = true)
    {
        Result<Transaction> result = ToTransaction(validateUserInput, gasCap, spec);
        Result<Transaction> ordered = CheckFeeCapOrder(result);
        if (checksFork && spec is not null && result.Success(out Transaction? tx, out _) && !IsTypeDefaulted && !spec.IsTxTypeEnabled(tx.Type))
            return ordered.IsError ? ordered : TxErrorMessages.InvalidTxType(spec.Name);
        return validateFeeCapOrder ? ordered : result;
    }

    private TxType ResolveType(IReleaseSpec? spec)
    {
        // Pre-Berlin only knows Legacy; defaulted-type requests downgrade to avoid EVM rejection.
        TxType type = Type ?? default;
        return spec is not null && !spec.IsEip2930Enabled && IsTypeDefaulted ? TxType.Legacy : type;
    }

    /// <summary>
    /// Validates fields required for signing (gas, fee, nonce) and returns the resulting <see cref="Transaction"/>.
    /// </summary>
    /// <remarks>As in geth, the fields pick the envelope: a request priced with <c>gasPrice</c> alone signs as legacy.</remarks>
    public Result<Transaction> ToSignableTransaction() =>
        MissingSigningField() is { } missing
            ? Result<Transaction>.Fail(missing)
            : ToValidatedTransaction();

    /// <summary>The first field signing needs that the request leaves out, or null when gas, fees and nonce are set.</summary>
    internal string? MissingSigningField() =>
        Gas is null ? "gas not specified"
        : !HasFeeFields(this) ? "missing gasPrice or maxFeePerGas/maxPriorityFeePerGas"
        // All concrete tx subtypes (AccessList, EIP1559, Blob, SetCode) derive from LegacyTransactionForRpc.
        : this is not LegacyTransactionForRpc { Nonce: not null } ? "nonce not specified"
        : null;

    private static bool HasFeeFields(TransactionForRpc rpcTx) =>
        rpcTx is EIP1559TransactionForRpc { MaxFeePerGas: not null, MaxPriorityFeePerGas: not null }
            or LegacyTransactionForRpc { GasPrice: not null };

    /// <summary>
    /// Fills the type-specific fields the caller left unset from node-computed defaults: each
    /// transaction type populates the fee model it uses, and blob transactions additionally derive
    /// the KZG sidecar from the supplied blobs.
    /// </summary>
    public virtual Result FillDefaults(in TxFillContext context) => Result.Success;

    public abstract bool ShouldSetBaseFee();

    public class TransactionJsonConverter : JsonConverter<TransactionForRpc>
    {
        private static readonly List<TxTypeInfo> _txTypes = [];
        private static readonly TxTypeInfo?[] _txTypesByType = new TxTypeInfo?[byte.MaxValue + 1];
        private static int _repopulatableSlotCount;
        private readonly GeneratedJsonDispatch _generated = new();
        private static Registry _registry = new([], [], [], [], []);

        // A snapshot of the registered types, newest first, and every discriminator name across them; TypesByName holds
        // per name the bitset of Types it indicates, NamesByType per type the bitset of Names it has a property for.
        // A read takes the whole snapshot, so a registration never mixes the indices of two generations.
        private sealed record Registry(TxTypeInfo[] Types, string[] Names, byte[][] NamesUtf8, ulong[] TypesByName, ulong[] NamesByType);

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

        /// <summary>Gets the RPC type registered for <paramref name="txType"/>, or <see langword="null"/> when none is.</summary>
        internal static Type? GetRegisteredType(TxType txType) => _txTypesByType[(byte)txType]?.Type;

        /// <summary>The number of slots allocated to registered types that support refilling.</summary>
        internal static int RepopulatableSlotCount => _repopulatableSlotCount;

        /// <summary>Gets the registered type's refill slot, or -1 when it does not support refilling.</summary>
        internal static int GetRepopulatableSlot(TxType txType) => _txTypesByType[(byte)txType]?.RepopulatableSlot ?? -1;

        internal static void RegisterTransactionType<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>() where T : TransactionForRpc, IFromTransaction<T>, ITxTyped
        {
            Type txType = typeof(T);
            string[] uniqueProperties = txType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.GetCustomAttribute<JsonDiscriminatorAttribute>() is not null)
                .Select(p => p.Name).ToArray();

            int slot = -1;
            if (txType.IsDefined(typeof(RepopulatableTransactionAttribute), inherit: false))
            {
                slot = GetRepopulatableSlot(T.TxType);
                if (slot < 0) slot = _repopulatableSlotCount++;
            }

            TxTypeInfo typeInfo = new()
            {
                TxType = T.TxType,
                Type = txType,
                RepopulatableSlot = slot,
                FromTransactionFunc = T.FromTransaction,
                DiscriminatorProperties = uniqueProperties,
                PropertyNames = Array.ConvertAll(txType.GetProperties(BindingFlags.Public | BindingFlags.Instance), static p => p.Name)
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

            _registry = BuildRegistry();
        }

        // Registration reorders the types and can add field names, so the registry is rebuilt each time.
        private static Registry BuildRegistry()
        {
            TxTypeInfo[] types = [.. _txTypes];
            string[] names = types.SelectMany(t => t.DiscriminatorProperties).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length > 64) throw new InvalidOperationException("Discriminator bitsets in DeriveTxType hold at most 64 field names.");

            ulong[] typesByName = new ulong[names.Length];
            ulong[] namesByType = new ulong[types.Length];
            for (int n = 0; n < names.Length; n++)
            {
                for (int i = 0; i < types.Length; i++)
                {
                    if (types[i].DiscriminatorProperties.Contains(names[n], StringComparer.OrdinalIgnoreCase))
                        typesByName[n] |= 1UL << i;
                    if (types[i].PropertyNames.Contains(names[n], StringComparer.OrdinalIgnoreCase))
                        namesByType[i] |= 1UL << n;
                }
            }

            return new Registry(types, names, Array.ConvertAll(names, static p => Encoding.UTF8.GetBytes(p.ToLowerInvariant())), typesByName, namesByType);
        }

        public override TransactionForRpc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Peek property names for the concrete type, then deserialize (no DOM).
            Utf8JsonReader txTypeReader = reader;

            Type concreteTxType = DeriveTxType(ref txTypeReader, options, out bool isDefaulted);

            TransactionForRpc? result = (TransactionForRpc?)TypeInfoJsonSerializer.Deserialize(ref reader, concreteTxType, options);
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
            Registry registry = _registry;
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
                        setType = TypeInfoJsonSerializer.Deserialize<TxType?>(ref reader, options);
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

            // As in geth, the fields pick the class for the Ethereum types up to set-code, so an explicit type never
            // drops a field or adds a requirement, and an unknown type is ignored. A registered type above set-code,
            // such as a chain extension, still picks its class, since its fields don't identify it.
            int index = -1;
            if (setType > TxType.SetCode)
            {
                for (int i = 0; i < registry.Types.Length; i++)
                {
                    if (registry.Types[i].TxType == setType)
                    {
                        index = i;
                        break;
                    }
                }
            }

            // Discriminator field is a strong signal — not a default. It wins over gasPrice, otherwise a
            // legacy-priced request would silently lose its accessList/blobVersionedHashes/authorizationList.
            // The selected type, explicit or else the newest indicated, must have every discriminator field seen; fields
            // no single type carries, such as blobVersionedHashes with authorizationList, are rejected rather than dropped.
            if (index != -1 || discriminated != 0)
            {
                if (index == -1) index = BitOperations.TrailingZeroCount(discriminated);
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

        public override void Write(Utf8JsonWriter writer, TransactionForRpc value, JsonSerializerOptions options) => writer.WriteAsRuntimeType(value, _generated, options);

        public static TransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData) => _txTypesByType[(byte)tx.Type]?.FromTransactionFunc(tx, extraData)
                ?? throw new ArgumentException("No converter for transaction type");

        class TxTypeInfo
        {
            public TxType TxType { get; set; }
            public Type Type { get; set; }
            public int RepopulatableSlot { get; set; } = -1;
            public FromTransactionFunc FromTransactionFunc { get; set; }
            public string[] DiscriminatorProperties { get; set; } = [];
            public string[] PropertyNames { get; set; } = [];
        }
    }

    public static TransactionForRpc FromTransaction(Transaction transaction, in TransactionForRpcContext? extraData = null) =>
        TransactionJsonConverter.FromTransaction(transaction, extraData ?? default);

    public static void RegisterTransactionType<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>() where T : TransactionForRpc, IFromTransaction<T>, ITxTyped => TransactionJsonConverter.RegisterTransactionType<T>();
}

/// <summary>
/// Marks fields that determine the transaction type
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class JsonDiscriminatorAttribute : Attribute
{
    public JsonDiscriminatorAttribute() { }
}

/// <summary>Marks an exact RPC transaction type whose Populate implementation resets every field for reuse.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class RepopulatableTransactionAttribute : Attribute;
