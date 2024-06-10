// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using System.Text.Json.Serialization;

namespace Nethermind.Optimism.Rpc;

public class OptimismReceiptForRpc : ReceiptForRpc
{
    public OptimismReceiptForRpc(Hash256 txHash, OptimismTxReceipt receipt, TxGasInfo gasInfo, L1TxGasInfo l1GasInfo, int logIndexStart = 0) : base(
        txHash, receipt, gasInfo, logIndexStart)
    {
        if (receipt.TxType == Core.TxType.DepositTx)
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
        }
    }

    // DepositTx related fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? DepositNonce;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? DepositReceiptVersion;

    // Regular tx fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? L1Fee;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? L1GasPrice;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? L1GasUsed;

    // Pre-ecotone field of a regular tx fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? L1FeeScalar;
}
