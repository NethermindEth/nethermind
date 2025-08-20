// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Metrics = Nethermind.Evm.Metrics;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockValidationTransactionsExecutor(
            // ITransactionProcessorAdapter transactionProcessor,
            ISpecProvider specProvider,
            IVirtualMachine virtualMachine,
            ICodeInfoRepository codeInfoRepository,
            IWorldState stateProvider,
            ILogManager logManager)
            : IBlockProcessor.IBlockTransactionsExecutor
        {
            public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;
            private BlockExecutionContext _blockExecutionContext;

            public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            {
                // transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
                _blockExecutionContext = blockExecutionContext;
            }

            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
            {
                Metrics.ResetBlockStats();

                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    block.TransactionProcessed = i;
                    Transaction currentTx = block.Transactions[i];
                    ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
                }
                return [.. receiptsTracer.TxReceipts];
            }

            protected virtual void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
            {
                // clone world state
                BlockAccessWorldState blockAccessStateProvider = new(block.DecodedBlockAccessList!.Value, (ushort)(index + 1), stateProvider);
                TransactionProcessor transactionProcessor = new(specProvider, blockAccessStateProvider, virtualMachine, codeInfoRepository, logManager);
                ExecuteTransactionProcessorAdapter transactionProcessorAdapter = new(transactionProcessor);
                transactionProcessorAdapter.SetBlockExecutionContext(_blockExecutionContext);
                TransactionResult result = transactionProcessorAdapter.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);
                if (!result) ThrowInvalidBlockException(result, block.Header, currentTx, index);
                TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
            }

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowInvalidBlockException(TransactionResult result, BlockHeader header, Transaction currentTx, int index)
            {
                throw new InvalidBlockException(header, $"Transaction {currentTx.Hash} at index {index} failed with error {result.Error}");
            }
        }
    }
}
