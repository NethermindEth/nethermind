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

            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockExecutionTracer executionTracer, IReleaseSpec spec)
            {
                Metrics.ResetBlockStats();
                BlockExecutionContext blkCtx = new(block.Header);
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction currentTx = block.Transactions[i];
                    ProcessTransaction(in blkCtx, currentTx, i, executionTracer, processingOptions);
                }
                return executionTracer.TxReceipts.ToArray();
            }

            private void ProcessTransaction(in BlockExecutionContext blkCtx, Transaction currentTx, int index, BlockExecutionTracer executionTracerr, ProcessingOptions processingOptions)
            {
                TransactionResult result = _transactionProcessor.ProcessTransaction(in blkCtx, currentTx, executionTracerr, processingOptions, _stateProvider);
                if (!result) ThrowInvalidBlockException(result, blkCtx.Header, currentTx, index);
                TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, executionTracerr.TxReceipts[index]));
            }

            public IBlockProcessor.IBlockTransactionsExecutor WithNewStateProvider(IWorldState worldState)
            {
                return new BlockValidationTransactionsExecutor(_transactionProcessor, worldState);
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
