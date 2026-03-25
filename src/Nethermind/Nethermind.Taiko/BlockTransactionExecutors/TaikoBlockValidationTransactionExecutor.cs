// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Config;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class TaikoBlockValidationTransactionExecutor(
        IWorldState stateProvider,
        ITransactionProcessorAdapter transactionProcessor,
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider specProvider,
        IBlockhashProvider blockhashProvider,
        ICodeInfoRepository codeInfoRepository,
        ILogManager logManager,
        IBlocksConfig blocksConfig)
    : BlockProcessor.BlockValidationTransactionsExecutor(stateProvider, transactionProcessor, blobBaseFeeCalculator, specProvider, blockhashProvider, codeInfoRepository, logManager, blocksConfig)
{
    protected override void ProcessTransaction(ITransactionProcessorAdapter transactionProcessor, Block block, Transaction currentTx, int i, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
    {
        if ((currentTx.SenderAddress?.Equals(TaikoBlockValidator.GoldenTouchAccount) ?? false) && i == 0)
            currentTx.IsAnchorTx = true;
        base.ProcessTransaction(transactionProcessor, block, currentTx, i, receiptsTracer, processingOptions);
    }
}
