// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Facade.Simulate;

// Wraps a chain's own factory so the EIP-7928 BAL processors it builds skip the EIP-3607 sender-code check.
public sealed class SkipSenderCodeCheckTransactionProcessorFactory(ITransactionProcessorFactory inner) : ITransactionProcessorFactory
{
    public ITransactionProcessor Create(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider specProvider,
        IWorldState worldState,
        IVirtualMachine virtualMachine,
        ICodeInfoRepository codeInfoRepository,
        ILogManager logManager,
        bool parallel)
    {
        ITransactionProcessor processor = inner.Create(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);
        if (processor is TransactionProcessorBase b) b.SkipSenderCodeCheck = true;
        return processor;
    }
}
