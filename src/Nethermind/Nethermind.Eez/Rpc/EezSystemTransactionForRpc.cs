// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Eez.Rpc;

/// <summary>
/// RPC view of an EEZ system transaction, with the fixed gas, zero price and zero signature fields that
/// Ethereum RPC consumers expect. Output only: system transactions are derived by the protocol, never submitted.
/// </summary>
public class EezSystemTransactionForRpc : TransactionForRpc, IFromTransaction<EezSystemTransactionForRpc>
{
    public static TxType TxType => EezConstants.SystemTxType;

    public override TxType? Type => TxType;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ulong? ChainId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ulong? Nonce { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? From { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? To { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? Value { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonConverter(typeof(StrictHexByteArrayConverter))]
    public byte[]? Input { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? GasPrice { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? V { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonConverter(typeof(NullableUInt256Converter))]
    public UInt256? R { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonConverter(typeof(NullableUInt256Converter))]
    public UInt256? S { get; set; }

    [JsonConstructor]
    public EezSystemTransactionForRpc() { }

    public EezSystemTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData)
    {
        ChainId = transaction.ChainId;
        Nonce = transaction.Nonce;
        From = transaction.SenderAddress;
        To = transaction.To;
        Value = transaction.Value;
        Input = transaction.Data.AsArray();
        Gas = transaction.GasLimit;
        GasPrice = UInt256.Zero;
        V = UInt256.Zero;
        R = UInt256.Zero;
        S = UInt256.Zero;
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null) =>
        Result<Transaction>.Fail("EEZ system transactions are derived by the protocol and cannot be submitted or simulated.");

    public override bool ShouldSetBaseFee() => false;

    public static EezSystemTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData) =>
        new(tx, extraData);
}
