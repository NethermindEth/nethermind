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
    public OptimismReceiptForRpc(
        Hash256 txHash,
        OptimismTxReceipt receipt,
        TxGasInfo gasInfo,
        L1TxGasInfo l1GasInfo,
        int logIndexStart = 0) : base(txHash, receipt, gasInfo, logIndexStart)
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
        }
    }

    public OptimismReceiptForRpc(
        Hash256 txHash,
        TxReceipt receipt,
        TxGasInfo gasInfo,
        int logIndexStart = 0) : base(txHash, receipt, gasInfo, logIndexStart)
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
}
