// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockValidationTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
        {
            private readonly ITransactionProcessorAdapter _transactionProcessor;
            private readonly IWorldState _stateProvider;

            public BlockValidationTransactionsExecutor(ITransactionProcessor transactionProcessor, IWorldState stateProvider)
                : this(new ExecuteTransactionProcessorAdapter(transactionProcessor), stateProvider)
            {
            }

            public BlockValidationTransactionsExecutor(ITransactionProcessorAdapter transactionProcessor, IWorldState stateProvider)
            {
                _transactionProcessor = transactionProcessor;
                _stateProvider = stateProvider;
            }

            public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;

            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
            {
                Evm.Metrics.ResetBlockStats();
                BlockExecutionContext blkCtx = new(block.Header);
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction currentTx = block.Transactions[i];
                    ProcessTransaction(blkCtx, currentTx, i, receiptsTracer, processingOptions);
                }
                return receiptsTracer.TxReceipts.ToArray();
            }

            private void ProcessTransaction(BlockExecutionContext blkCtx, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
            {
                _transactionProcessor.ProcessTransaction(blkCtx, currentTx, receiptsTracer, processingOptions, _stateProvider);
                TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
            }
        }
    }
}
