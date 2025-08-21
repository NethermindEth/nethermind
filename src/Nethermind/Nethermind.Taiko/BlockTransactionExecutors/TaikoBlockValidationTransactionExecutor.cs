// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class TaikoBlockValidationTransactionExecutor(
    ISpecProvider specProvider,
    IVirtualMachine virtualMachine,
    ICodeInfoRepository codeInfoRepository,
    IWorldState stateProvider,
    ILogManager logManager)
    : BlockProcessor.BlockValidationTransactionsExecutor(specProvider, virtualMachine, codeInfoRepository, stateProvider, logManager)
{
    protected override void ProcessTransaction(Block block, Transaction currentTx, int i, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
    {
        if ((currentTx.SenderAddress?.Equals(TaikoBlockValidator.GoldenTouchAccount) ?? false) && i == 0)
            currentTx.IsAnchorTx = true;
        base.ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
    }
}
