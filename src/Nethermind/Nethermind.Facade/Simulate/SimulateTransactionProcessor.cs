// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Facade.Simulate;

public class SimulateTransactionProcessor(
    SimulateRequestState simulateState,
    ISpecProvider? specProvider,
    IWorldState? worldState,
    IVirtualMachine? virtualMachine,
    ICodeInfoRepository? codeInfoRepository,
    ILogManager? logManager) : TransactionProcessorBase(specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
{
    protected override bool TryCalculateBlobBaseFee(BlockHeader header, Transaction transaction, UInt256 blobGasPriceUpdateFraction,
        out UInt256 blobBaseFee)
    {
        if (simulateState.BlobBaseFeeOverride is not null)
        {
            return !UInt256.MultiplyOverflow(BlobGasCalculator.CalculateBlobGas(transaction), simulateState.BlobBaseFeeOverride.Value,
                out blobBaseFee);
        }

        return base.TryCalculateBlobBaseFee(header, transaction, blobGasPriceUpdateFraction, out blobBaseFee);
    }
}
