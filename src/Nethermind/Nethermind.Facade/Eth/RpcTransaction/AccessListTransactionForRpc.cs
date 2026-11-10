// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth.RpcTransaction;

[GenerateJsonWriter(RegisterWithSerializer = false)]
[RepopulatableTransaction]
public class AccessListTransactionForRpc : LegacyTransactionForRpc, IFromTransaction<AccessListTransactionForRpc>
{
    public new static TxType TxType => TxType.AccessList;

    public override TxType? Type => TxType;

    [JsonDiscriminator]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public AccessListForRpc? AccessList { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public virtual UInt256? YParity { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public sealed override ulong? ChainId { get; set; }

    [JsonConstructor]
    public AccessListTransactionForRpc() { }

    public AccessListTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData) { }

    internal override void Populate(Transaction transaction, in TransactionForRpcContext extraData)
    {
        base.Populate(transaction, extraData);
        AccessList = AccessListForRpc.FromAccessList(transaction.AccessList);
        YParity = transaction.Signature?.RecoveryId ?? 0;
        ChainId = transaction.ChainId ?? extraData.ChainId ?? BlockchainIds.Mainnet;
        V = YParity ?? 0;
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null)
    {
        Result<Transaction> baseResult = base.ToTransaction(validateUserInput, gasCap, spec);
        if (baseResult.IsError) return baseResult;

        Transaction tx = baseResult.Data;

        if (tx.SupportsAccessList)
        {
            tx.AccessList = AccessList?.ToAccessList() ?? Core.Eip2930.AccessList.Empty;
        }

        return tx;
    }

    internal override string? GetTraceCallForkError(IReleaseSpec spec, UInt256 baseFee) =>
        GetTraceCallUnsupportedField(spec) is { } unsupported
            ? GetTraceCallPrecheckError(unsupported, spec, baseFee)
            : null;

    // Inspect RPC fields, not the inferred envelope: conversion supplies empty access lists for newer types.
    internal virtual string? GetTraceCallUnsupportedField(IReleaseSpec spec) =>
        AccessList is not null && !spec.IsEip2930Enabled ? "access list" : null;

    internal virtual string GetTraceCallPrecheckError(string? unsupported, IReleaseSpec spec, UInt256 baseFee) =>
        GetTraceCallExecutionError(unsupported, spec, baseFee, GasPrice ?? UInt256.Zero, GasPrice ?? UInt256.Zero);

    internal string GetTraceCallExecutionError(string? unsupported, IReleaseSpec spec, UInt256 baseFee, UInt256 feeCap, UInt256 tipCap)
    {
        string sender = (From ?? Address.Zero).ToString(withEip55Checksum: true);
        // Geth's message precheck rejects fees before unsupported fields, and fields before intrinsic gas or balance.
        if (spec.IsEip1559Enabled)
        {
            if (feeCap < tipCap)
                return RpcTransactionErrors.DebugTrace($"{TxErrorMessages.TipAboveFeeCap}: address {sender}, maxPriorityFeePerGas: {tipCap}, maxFeePerGas: {feeCap}");
            if (feeCap < baseFee && (!feeCap.IsZero || !tipCap.IsZero))
                return RpcTransactionErrors.DebugTrace($"{RpcTransactionErrors.FeeCapBelowBaseFee}: address {sender}, maxFeePerGas: {feeCap}, baseFee: {baseFee}");
        }
        return RpcTransactionErrors.DebugTrace($"transaction type not supported: {unsupported} tx (sender {sender})");
    }

    public new static AccessListTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);
}
