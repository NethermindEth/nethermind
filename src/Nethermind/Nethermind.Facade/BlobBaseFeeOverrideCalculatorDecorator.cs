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
    public bool TryCalculateBlobBaseFee(
        BlockHeader header,
        Transaction transaction,
        UInt256 blobGasPriceUpdateFraction,
        out UInt256 blobBaseFee) =>
            overrideProvider.BlobBaseFeeOverride is not null
                ? !UInt256.MultiplyOverflow(BlobGasCalculator.CalculateBlobGas(transaction), overrideProvider.BlobBaseFeeOverride.Value, out blobBaseFee)
            : blobBaseFeeCalculatorBase.TryCalculateBlobBaseFee(header, transaction, blobGasPriceUpdateFraction, out blobBaseFee);
}
