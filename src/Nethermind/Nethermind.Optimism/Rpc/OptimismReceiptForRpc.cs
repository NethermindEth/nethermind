// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Optimism.Rpc;

public class OptimismReceiptForRpc : ReceiptForRpc
{
    public OptimismReceiptForRpc()
    {
    }

    public OptimismReceiptForRpc(
        Hash256 txHash,
        OptimismTxReceipt receipt,
        ulong blockTimestamp,
        TxGasInfo gasInfo,
        L1TxGasInfo l1GasInfo,
        int logIndexStart = 0) : base(txHash, receipt, blockTimestamp, gasInfo, logIndexStart) =>
        SetOptimismFields(receipt, l1GasInfo);

    /// <summary>Refills this instance as the constructor for <paramref name="receipt"/>'s type builds it.</summary>
    /// <param name="l1GasInfo">The transaction's L1 gas, required for an <see cref="OptimismTxReceipt"/>.</param>
    internal void Populate(Hash256 txHash, TxReceipt receipt, ulong blockTimestamp, TxGasInfo gasInfo, L1TxGasInfo? l1GasInfo, int logIndexStart)
    {
        Populate(txHash, receipt, blockTimestamp, gasInfo, logIndexStart);
        DepositNonce = null;
        DepositReceiptVersion = null;
        L1Fee = null;
        L1GasPrice = null;
        L1GasUsed = null;
        L1FeeScalar = null;
        L1BaseFeeScalar = null;
        L1BlobBaseFee = null;
        L1BlobBaseFeeScalar = null;
        OperatorFeeScalar = null;
        OperatorFeeConstant = null;
        DaFootprintGasScalar = null;
        if (receipt is OptimismTxReceipt optimismReceipt && l1GasInfo is { } info) SetOptimismFields(optimismReceipt, info);
    }

    private void SetOptimismFields(OptimismTxReceipt receipt, L1TxGasInfo l1GasInfo)
    {
        if (receipt.TxType == TxType.DepositTx)
        {
            DepositNonce = receipt.DepositNonce;
            DepositReceiptVersion = receipt.DepositReceiptVersion;
        }
        else
        {
            L1Fee = l1GasInfo.L1Fee;
            L1GasUsed = l1GasInfo.L1GasUsed;
            L1GasPrice = l1GasInfo.L1GasPrice;
            L1FeeScalar = l1GasInfo.L1FeeScalar;

            L1BaseFeeScalar = l1GasInfo.L1BaseFeeScalar;
            L1BlobBaseFee = l1GasInfo.L1BlobBaseFee;
            L1BlobBaseFeeScalar = l1GasInfo.L1BlobBaseFeeScalar;

            OperatorFeeScalar = l1GasInfo.OperatorFeeScalar;
            OperatorFeeConstant = l1GasInfo.OperatorFeeConstant;

            if (l1GasInfo.DaFootprintGasScalar is not null)
            {
                DaFootprintGasScalar = l1GasInfo.DaFootprintGasScalar;
                BlobGasUsed = l1GasInfo.DaFootprint;
            }
        }
    }

    public OptimismReceiptForRpc(
        Hash256 txHash,
        TxReceipt receipt,
        ulong blockTimestamp,
        TxGasInfo gasInfo,
        int logIndexStart = 0) : base(txHash, receipt, blockTimestamp, gasInfo, logIndexStart)
    {
    }

    // DepositTx related fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? DepositNonce { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? DepositReceiptVersion { get; set; }


    // Regular tx fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? L1Fee { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? L1GasPrice { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? L1GasUsed { get; set; }

    // Pre-ecotone field of a regular tx fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? L1FeeScalar { get; set; }

    // Fjord fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? L1BaseFeeScalar { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? L1BlobBaseFee { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? L1BlobBaseFeeScalar { get; set; }

    // Isthmus fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? OperatorFeeScalar { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? OperatorFeeConstant { get; set; }

    // Jovian fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? DaFootprintGasScalar { get; set; }
}
