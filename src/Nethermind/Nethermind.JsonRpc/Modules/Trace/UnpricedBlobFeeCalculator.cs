// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Trace;

/// <summary>
/// Prices the blob gas of a blob transaction with a zero blob fee cap at zero, as <see cref="UnpricedCallTraceAdapter"/>
/// runs it at a zero blob base fee, and that of any other transaction as the decorated calculator does.
/// </summary>
internal sealed class UnpricedBlobFeeCalculator(ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator)
    : ITransactionProcessor.IBlobBaseFeeCalculator
{
    public bool TryCalculateBlobFees(BlockHeader header, Transaction transaction,
        ulong blobGasPriceUpdateFraction, out UInt256 feePerBlobGas, out UInt256 totalBlobBaseFee)
    {
        if (!UnpricedCallTraceAdapter.HasUnpricedBlobs(transaction))
            return blobBaseFeeCalculator.TryCalculateBlobFees(header, transaction, blobGasPriceUpdateFraction, out feePerBlobGas, out totalBlobBaseFee);

        feePerBlobGas = UInt256.Zero;
        totalBlobBaseFee = UInt256.Zero;
        return true;
    }
}
