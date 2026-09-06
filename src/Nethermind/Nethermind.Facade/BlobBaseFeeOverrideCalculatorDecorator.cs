// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Facade;

public class BlobBaseFeeOverrideCalculatorDecorator(
    ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculatorBase,
    IBlobBaseFeeOverrideProvider overrideProvider) : ITransactionProcessor.IBlobBaseFeeCalculator
{
    public bool TryCalculateBlobFees(BlockHeader header, Transaction transaction,
        ulong blobGasPriceUpdateFraction, out UInt256 feePerBlobGas, out UInt256 totalBlobBaseFee)
    {
        if (overrideProvider.BlobBaseFeeOverride is not null)
        {
            feePerBlobGas = overrideProvider.BlobBaseFeeOverride.Value;
            return !UInt256.MultiplyOverflow(BlobGasCalculator.CalculateBlobGas(transaction), feePerBlobGas, out totalBlobBaseFee);
        }
        return blobBaseFeeCalculatorBase.TryCalculateBlobFees(header, transaction, blobGasPriceUpdateFraction, out feePerBlobGas, out totalBlobBaseFee);
    }
}
