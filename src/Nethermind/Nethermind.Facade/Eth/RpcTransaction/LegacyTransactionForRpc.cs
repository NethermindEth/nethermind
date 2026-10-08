// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
// ReSharper disable VirtualMemberCallInConstructor

namespace Nethermind.Facade.Eth.RpcTransaction;

[GenerateJsonWriter(RegisterWithSerializer = false)]
[RepopulatableTransaction]
public class LegacyTransactionForRpc : SignableTransactionForRpc, ITxTyped, IFromTransaction<LegacyTransactionForRpc>, IJsonOnDeserializing, IJsonOnDeserialized
{
    public static TxType TxType => TxType.Legacy;

    public override TxType? Type => TxType;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ulong? Nonce { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? To { get; set; }

    // NOTE: This field exists only during deserialization according to the Ethereum JSON-RPC spec.
    // No transaction types include a `From` field when serializing.
    // For backwards compatibility with previous Nethermind versions we also serialize it.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? From { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? Value { get; set; }

    /// <summary>Sets calldata through the legacy alias for <see cref="Input"/>.</summary>
    /// <remarks>
    /// Accepted during deserialization for compatibility with clients such as Prysm, but never serialized.
    /// When JSON supplies both aliases, their non-null values must be equal; assignments in code update <see cref="Input"/> directly.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(StrictHexByteArrayConverter))]
    public byte[]? Data
    {
        private get => null;
        set
        {
            if (_isDeserializing) _data = value ?? _data;
            else Input = value;
        }
    }

    private byte[]? _data;
    private bool _isDeserializing;

    /// <remarks>
    /// <see cref="Data"/> is an alias when deserializing: a request may set either or both, but both must be equal.
    /// An explicit JSON null for either is the same as omitting it.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonConverter(typeof(StrictHexByteArrayConverter))]
    public byte[]? Input { get => _input; set => _input = value ?? _input; }

    private byte[]? _input;

    void IJsonOnDeserializing.OnDeserializing() => _isDeserializing = true;

    void IJsonOnDeserialized.OnDeserialized()
    {
        _isDeserializing = false;
        if (_data is null) return;
        if (Input is not null && !Input.AsSpan().SequenceEqual(_data))
            throw new SafePublicMessageFormatException(RpcTransactionErrors.DataAndInputDiffer);

        Input = _data;
        _data = null;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public virtual UInt256? GasPrice { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public virtual ulong? ChainId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public virtual UInt256? V { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonConverter(typeof(NullableUInt256Converter))]
    public virtual UInt256? R { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonConverter(typeof(NullableUInt256Converter))]
    public virtual UInt256? S { get; set; }

    [JsonConstructor]
    public LegacyTransactionForRpc() { }

    public LegacyTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData) { }

    internal override void Populate(Transaction transaction, in TransactionForRpcContext extraData)
    {
        base.Populate(transaction, extraData);
        _data = null;
        _isDeserializing = false;
        Nonce = transaction.Nonce;
        To = transaction.To;
        From = transaction.SenderAddress;
        Gas = transaction.GasLimit;
        Value = transaction.Value;
        _input = transaction.Data.AsArray();
        GasPrice = transaction.GasPrice;

        Signature? signature = transaction.Signature;
        if (signature is null)
        {
            R = UInt256.Zero;
            S = UInt256.Zero;
            V = 0;
            ChainId = transaction.ChainId;
        }
        else
        {
            R = new UInt256(signature.R.Span, true);
            S = new UInt256(signature.S.Span, true);
            V = signature.V;
            ChainId = transaction.ChainId ?? signature.ChainId;
        }
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null)
    {
        if (validateUserInput && Type?.SupportsFrames() != true && To is null && Input is null or { Length: 0 })
            return RpcTransactionErrors.ContractCreationWithoutData;

        Result<Transaction> baseResult = base.ToTransaction(validateUserInput, gasCap, spec);
        if (baseResult.IsError) return baseResult;

        Transaction tx = baseResult.Data;
        tx.Nonce = Nonce ?? 0UL; // TODO: Should we pick the last nonce?
        tx.To = To;
        tx.Value = Value ?? UInt256.Zero;
        tx.Data = Input;
        tx.GasPrice = GasPrice ?? UInt256.Zero;
        tx.ChainId = ChainId;
        tx.SenderAddress = From ?? Address.Zero;

        // Omitted gas falls back to the lower of the RPC gas cap and the processor-enforced cap; an explicit gas
        // is only lowered to the RPC gas cap, so a request above the processor-enforced cap still fails validation.
        // Explicit zero is a literal request that fails the intrinsic gas check, not a missing-gas default.
        ulong effectiveCap = gasCap.EffectiveGasCap();
        ulong processorCap = spec?.GetProcessorEnforcedTxGasLimitCap() ?? ulong.MaxValue;
        tx.GasLimit = Gas is null
            ? Math.Min(effectiveCap, processorCap)
            : Math.Min(Gas.Value, effectiveCap);

        if ((R?.IsZero == false || S?.IsZero == false) && (R is not null || S is not null))
        {
            ulong v = V is null ? 0
                : V.Value > 1
                    ? V.Value.ToUInt64(null) // non protected
                    : EthereumEcdsaExtensions.CalculateV(ChainId ?? 0, V.Value == 1); // protected

            tx.Signature = new(R ?? UInt256.Zero, S ?? UInt256.Zero, v);
        }

        return tx;
    }

    public override bool ShouldSetBaseFee() => GasPrice.IsPositive();

    public override Result FillDefaults(in TxFillContext context)
    {
        GasPrice ??= context.GasPrice;
        return Result.Success;
    }

    public static LegacyTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData) =>
        new(tx, extraData);
}
