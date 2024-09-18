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
        public class BlockValidationTransactionsExecutor(
            ITransactionProcessorAdapter transactionProcessor)
            : IBlockProcessor.IBlockTransactionsExecutor
        {
            public BlockValidationTransactionsExecutor(ITransactionProcessor transactionProcessor)
                : this(new ExecuteTransactionProcessorAdapter(transactionProcessor))
            {
            }

            public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;

            public TxReceipt[] ProcessTransactions(IWorldState worldState, Block block,
                ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
            {
                Metrics.ResetBlockStats();
                BlockExecutionContext blkCtx = CreateBlockExecutionContext(block);
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    block.TransactionProcessed = i;
                    Transaction currentTx = block.Transactions[i];
                    ProcessTransaction(worldState, in blkCtx, currentTx, i, receiptsTracer, processingOptions);
                }
                return receiptsTracer.TxReceipts.ToArray();
            }

            protected virtual BlockExecutionContext CreateBlockExecutionContext(Block block) => new(block.Header);

            protected virtual void ProcessTransaction(IWorldState worldState, in BlockExecutionContext blkCtx,
                Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer,
                ProcessingOptions processingOptions)
            {
                TransactionResult result = transactionProcessor.ProcessTransaction(in blkCtx, currentTx, receiptsTracer, processingOptions, worldState);
                if (!result) ThrowInvalidBlockException(result, blkCtx.Header, currentTx, index);
                TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
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
