// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    public class BlockStatelessValidationTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
    {
        private ITransactionProcessorAdapter _transactionProcessor;

        public BlockStatelessValidationTransactionsExecutor(ITransactionProcessor transactionProcessor)
            : this(new ExecuteTransactionProcessorAdapter(transactionProcessor))
        {
        }

        public BlockStatelessValidationTransactionsExecutor(ITransactionProcessorAdapter transactionProcessor)
        {
            _transactionProcessor = transactionProcessor;
        }

        public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;

        public IBlockProcessor.IBlockTransactionsExecutor WithNewStateProvider()
        {
            _transactionProcessor = _transactionProcessor.WithNewStateProvider();
            return new BlockStatelessValidationTransactionsExecutor(_transactionProcessor);
        }

        public TxReceipt[] ProcessTransactions(IWorldState worldState, Block block, ProcessingOptions processingOptions, BlockExecutionTracer receiptsTracer, IReleaseSpec spec)
        {
            Evm.Metrics.ResetBlockStats();
            if (!block.IsGenesis)
            {
                BlockExecutionContext blkCtx = new(block.Header);
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction currentTx = block.Transactions[i];
                    ProcessTransaction(worldState, blkCtx, currentTx, i, receiptsTracer, processingOptions);
                }
                worldState.Commit(spec);
                worldState.RecalculateStateRoot();
            }
            return receiptsTracer.TxReceipts.ToArray();
        }

        private void ProcessTransaction(IWorldState worldState, BlockExecutionContext blkCtx, Transaction currentTx, int index, BlockExecutionTracer executionTracer, ProcessingOptions processingOptions)
        {
            _transactionProcessor.ProcessTransaction(blkCtx, currentTx, executionTracer, processingOptions, worldState);
            TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, executionTracer.TxReceipts[index]));
        }
    }
}
