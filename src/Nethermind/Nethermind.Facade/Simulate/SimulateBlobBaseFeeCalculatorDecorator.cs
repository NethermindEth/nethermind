// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Facade.Simulate;

public class SimulateBlobBaseFeeCalculatorDecorator(ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculatorBase, SimulateRequestState simulateState) : ITransactionProcessor.IBlobBaseFeeCalculator
{
    public bool TryCalculateBlobBaseFee(BlockHeader header, Transaction transaction, UInt256 blobGasPriceUpdateFraction,
        out UInt256 blobBaseFee)
    {
        if (simulateState.BlobBaseFeeOverride is not null)
        {
            return !UInt256.MultiplyOverflow(BlobGasCalculator.CalculateBlobGas(transaction), simulateState.BlobBaseFeeOverride.Value,
                out blobBaseFee);
        }

        return blobBaseFeeCalculatorBase.TryCalculateBlobBaseFee(header, transaction, blobGasPriceUpdateFraction, out blobBaseFee);
    }
}
