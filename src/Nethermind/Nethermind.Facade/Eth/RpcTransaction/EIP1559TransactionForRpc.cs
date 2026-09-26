// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class EIP1559TransactionForRpc : AccessListTransactionForRpc, IFromTransaction<EIP1559TransactionForRpc>
{
    public new static TxType TxType => TxType.EIP1559;

    public override TxType? Type => TxType.EIP1559;

    [JsonDiscriminator]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? MaxPriorityFeePerGas { get; set; }

    [JsonDiscriminator]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? MaxFeePerGas { get; set; }

    [JsonConstructor]
    public EIP1559TransactionForRpc() { }

    public EIP1559TransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData)
    {
        MaxFeePerGas = transaction.MaxFeePerGas;
        MaxPriorityFeePerGas = transaction.MaxPriorityFeePerGas;
        // ReSharper disable once VirtualMemberCallInConstructor
        GasPrice = extraData.BaseFee is not null
            ? transaction.CalculateEffectiveGasPrice(eip1559Enabled: true, extraData.BaseFee.Value)
            : transaction.MaxFeePerGas;
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null, bool validateFeeCapOrder = true)
    {
        if (validateUserInput)
        {
            // Reject ambiguous input: both gasPrice and EIP-1559 fields
            if (GasPrice is not null && (MaxFeePerGas is not null || MaxPriorityFeePerGas is not null))
                return RpcTransactionErrors.GasPriceInEip1559;

            if (validateFeeCapOrder && MaxFeePerGas < MaxPriorityFeePerGas)
                return RpcTransactionErrors.MaxFeePerGasSmallerThanMaxPriorityFeePerGas(MaxFeePerGas, MaxPriorityFeePerGas);
        }

        Result<Transaction> baseResult = base.ToTransaction(validateUserInput, gasCap, spec, validateFeeCapOrder);
        if (baseResult.IsError) return baseResult;

        Transaction tx = baseResult.Data;

        if (tx.Supports1559)
        {
            // A dynamic-fee type priced with gasPrice: the single price stands in for the missing fee cap and tip.
            tx.GasPrice = MaxPriorityFeePerGas ?? GasPrice ?? UInt256.Zero;
            tx.DecodedMaxFeePerGas = MaxFeePerGas ?? GasPrice ?? UInt256.Zero;
        }

        return tx;
    }

    public override bool ShouldSetBaseFee() =>
        base.ShouldSetBaseFee() || MaxFeePerGas.IsPositive() || MaxPriorityFeePerGas.IsPositive();

    public override Result FillDefaults(in TxFillContext context)
    {
        if (GasPrice is { } gasPrice)
        {
            if (MaxFeePerGas is not null || MaxPriorityFeePerGas is not null)
                return RpcTransactionErrors.GasPriceInEip1559;

            // Same gasPrice pricing as ToTransaction, made explicit so the filled tx validates as-is.
            MaxPriorityFeePerGas = gasPrice;
            MaxFeePerGas = gasPrice;
            GasPrice = null;
        }

        MaxPriorityFeePerGas ??= context.MaxPriorityFeePerGas;
        MaxFeePerGas ??= context.BaseFee * 2 + MaxPriorityFeePerGas.Value;
        return Result.Success;
    }

    public new static EIP1559TransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);
}
