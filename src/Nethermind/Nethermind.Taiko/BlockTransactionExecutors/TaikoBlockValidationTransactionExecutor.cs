// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Taiko.Precompiles;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class TaikoBlockValidationTransactionExecutor(
    ITransactionProcessorAdapter transactionProcessor,
    IWorldState stateProvider,
    IL1OriginStore l1OriginStore,
    ILogManager logManager)
    : BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider)
{
    private readonly ILogger _logger = logManager.GetClassLogger<TaikoBlockValidationTransactionExecutor>();

    public override TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        try
        {
            return base.ProcessTransactions(block, processingOptions, receiptsTracer, token);
        }
        finally
        {
            L1PrecompileExecutionContext.Clear();
        }
    }

    protected override void ProcessTransaction(Block block, Transaction currentTx, int i, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
    {
        if ((currentTx.SenderAddress?.Equals(TaikoBlockValidator.GoldenTouchAccount) ?? false) && i == 0)
            currentTx.IsAnchorTx = true;

        // Parse anchor context before base.ProcessTransaction so any L1 precompile calls in the
        // block see the 256-block window — mirrors BlockInvalidTxExecutor. The parse reads
        // calldata only; the method early-returns for i!=0 or non-AnchorV4 selectors.
        if (i == 0 && !L1PrecompileContextInitializer.TrySetFromAnchorTransaction(i, currentTx, block.Header.Number, l1OriginStore)
            && _logger.IsWarn)
        {
            _logger.Warn($"TaikoBlockValidationTransactionExecutor: anchor tx context not set at block {block.Header.Number} — subsequent L1 precompile calls will skip range validation");
        }

        base.ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
    }
}
