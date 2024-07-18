// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;
using Metrics = Nethermind.Evm.Metrics;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockValidationTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
        {
            private readonly ITransactionProcessorAdapter _transactionProcessor;

            public BlockValidationTransactionsExecutor(ITransactionProcessor transactionProcessor)
                : this(new ExecuteTransactionProcessorAdapter(transactionProcessor))
            {
            }

            public BlockValidationTransactionsExecutor(ITransactionProcessorAdapter transactionProcessor)
            {
                _transactionProcessor = transactionProcessor;
            }

            public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;

            public TxReceipt[] ProcessTransactions(IWorldState worldState, Block block, ProcessingOptions processingOptions, BlockExecutionTracer executionTracer, IReleaseSpec spec)
            {
                Metrics.ResetBlockStats();
                BlockExecutionContext blkCtx = new(block.Header);
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction currentTx = block.Transactions[i];
                    ProcessTransaction(worldState, in blkCtx, currentTx, i, executionTracer, processingOptions);
                }
                return executionTracer.TxReceipts.ToArray();
            }

            private void ProcessTransaction(IWorldState worldState, in BlockExecutionContext blkCtx, Transaction currentTx, int index, BlockExecutionTracer executionTracerr, ProcessingOptions processingOptions)
            {
                TransactionResult result = _transactionProcessor.ProcessTransaction(in blkCtx, currentTx, executionTracerr, processingOptions, worldState);
                if (!result) ThrowInvalidBlockException(result, blkCtx.Header, currentTx, index);
                TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, executionTracerr.TxReceipts[index]));
            }

            public IBlockProcessor.IBlockTransactionsExecutor WithNewStateProvider()
            {
                return new BlockValidationTransactionsExecutor(_transactionProcessor);
            }

            [DoesNotReturn]
            [StackTraceHidden]
            private void ThrowInvalidBlockException(TransactionResult result, BlockHeader header, Transaction currentTx, int index)
            {
                throw new InvalidBlockException(header, $"Transaction {currentTx.Hash} at index {index} failed with error {result.Error}");
            }
        }
    }
}
