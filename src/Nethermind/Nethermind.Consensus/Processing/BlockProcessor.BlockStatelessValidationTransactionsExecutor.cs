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
        private IWorldState? _stateProvider;

        public BlockStatelessValidationTransactionsExecutor(ITransactionProcessor transactionProcessor)
            : this(new ExecuteTransactionProcessorAdapter(transactionProcessor))
        {
        }

        public BlockStatelessValidationTransactionsExecutor(ITransactionProcessorAdapter transactionProcessor)
        {
            _transactionProcessor = transactionProcessor;
            _stateProvider = null;
        }

        public BlockStatelessValidationTransactionsExecutor(ITransactionProcessor transactionProcessor, IWorldState worldState)
            : this(new ExecuteTransactionProcessorAdapter(transactionProcessor), worldState)
        {
        }

        public BlockStatelessValidationTransactionsExecutor(ITransactionProcessorAdapter transactionProcessor, IWorldState worldState)
        {
            _transactionProcessor = transactionProcessor;
            _stateProvider = worldState;
        }

        public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;

        public IBlockProcessor.IBlockTransactionsExecutor WithNewStateProvider(IWorldState worldState)
        {
            _transactionProcessor = _transactionProcessor.WithNewStateProvider(worldState);
            return new BlockStatelessValidationTransactionsExecutor(_transactionProcessor, worldState);
        }

        public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockExecutionTracer receiptsTracer, IReleaseSpec spec)
        {
            Evm.Metrics.ResetBlockStats();
            if (!block.IsGenesis)
            {
                BlockExecutionContext blkCtx = new(block.Header);
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    Transaction currentTx = block.Transactions[i];
                    ProcessTransaction(blkCtx, currentTx, i, receiptsTracer, processingOptions);
                }
                _stateProvider!.Commit(spec);
                _stateProvider!.RecalculateStateRoot();
            }
            return receiptsTracer.TxReceipts.ToArray();
        }

        private void ProcessTransaction(BlockExecutionContext blkCtx, Transaction currentTx, int index, BlockExecutionTracer executionTracer, ProcessingOptions processingOptions)
        {
            _transactionProcessor.ProcessTransaction(blkCtx, currentTx, executionTracer, processingOptions, _stateProvider!);
            TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, executionTracer.TxReceipts[index]));
        }
    }
}
