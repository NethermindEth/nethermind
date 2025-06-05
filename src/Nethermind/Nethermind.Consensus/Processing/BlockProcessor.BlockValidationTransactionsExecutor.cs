// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
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
            ITransactionProcessorAdapter transactionProcessor,
            IWorldState stateProvider)
            : IBlockProcessor.IBlockTransactionsExecutor
        {
            public BlockValidationTransactionsExecutor(ITransactionProcessor transactionProcessor, IWorldState stateProvider)
                : this(new ExecuteTransactionProcessorAdapter(transactionProcessor), stateProvider)
            {
            }

            public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;

            public TxReceipt[] ProcessTransactions(Block block, in BlockExecutionContext blkCtx, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec, CancellationToken token)
            {
                Metrics.ResetBlockStats();

                var enhanced = EnhanceBlockExecutionContext(blkCtx);

                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    block.TransactionProcessed = i;
                    Transaction currentTx = block.Transactions[i];
                    ProcessTransaction(in enhanced, currentTx, i, receiptsTracer, processingOptions);
                }
                return receiptsTracer.TxReceipts.ToArray();
            }

            protected virtual BlockExecutionContext EnhanceBlockExecutionContext(in BlockExecutionContext blkCtx) => blkCtx;

            protected virtual void ProcessTransaction(in BlockExecutionContext blkCtx, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
            {
                TransactionResult result = transactionProcessor.ProcessTransaction(in blkCtx, currentTx, receiptsTracer, processingOptions, stateProvider);
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
