// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism;

public class OptimismReceiptForRpc : ReceiptForRpc
{
    public OptimismReceiptForRpc(Hash256 txHash, OptimismTxReceipt receipt, TxGasInfo gasInfo, L1GasInfo l1GasInfo, int logIndexStart = 0) : base(
        txHash, receipt, gasInfo, logIndexStart)
    {
        L1BaseFeeScalar = l1GasInfo.L1BaseFeeScalar;
        L1BlobBaseFee = l1GasInfo.L1BlobBaseFee;
        L1BlobBaseFeeScalar = l1GasInfo.L1BlobBaseFeeScalar;
        L1Fee = l1GasInfo.L1Fee;
        L1GasPrice = l1GasInfo.L1GasPrice;
        L1GasUsed = l1GasInfo.L1GasUsed;
    }

    public UInt256? DepositNonce;
    public UInt256? DepositReceiptVersion;

    public UInt256? L1BaseFeeScalar;
    public UInt256? L1BlobBaseFee;
    public UInt256? L1BlobBaseFeeScalar;
    public UInt256? L1Fee;
    public UInt256? L1GasPrice;
    public UInt256? L1GasUsed;
}
