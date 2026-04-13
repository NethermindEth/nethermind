// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Messages;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Metrics = Nethermind.Evm.Metrics;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockValidationTransactionsExecutor(
            ITransactionProcessorAdapter transactionProcessor,
            IWorldState stateProvider,
            BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
            : IBlockProcessor.IBlockTransactionsExecutor
        {
            private readonly IBlockAccessListBuilder? _balBuilder = stateProvider as IBlockAccessListBuilder;

            public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            {
                transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
            }

            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
            {
                Metrics.ResetBlockStats();

                bool shouldValidateBlockAccessList = _balBuilder is not null && !processingOptions.ContainsFlag(ProcessingOptions.NoValidation);
                long gasRemaining = shouldValidateBlockAccessList ? block.Header.GasLimit : 0;

                if (shouldValidateBlockAccessList)
                {
                    _balBuilder!.ValidateBlockAccessList(block.Header, 0, gasRemaining);
                }

                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    _balBuilder?.GeneratedBlockAccessList.IncrementBlockAccessIndex();
                    Transaction currentTx = block.Transactions[i];
                    ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);

                    if (!processingOptions.ContainsFlag(ProcessingOptions.NoValidation) && block.Header.GasUsed > block.Header.GasLimit)
                    {
                        throw new InvalidBlockException(block, BlockErrorMessages.ExceededGasLimit);
                    }

                    if (shouldValidateBlockAccessList)
                    {
                        gasRemaining -= currentTx.BlockGasUsed;
                        _balBuilder!.ValidateBlockAccessList(block.Header, (ushort)(i + 1), gasRemaining);
                    }
                }
                _balBuilder?.GeneratedBlockAccessList.IncrementBlockAccessIndex();

                return [.. receiptsTracer.TxReceipts];
            }

            protected virtual void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
            {
                TransactionResult result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);
                if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);
                transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));
            }

            [DoesNotReturn, StackTraceHidden]
            private void ThrowInvalidTransactionException(TransactionResult result, BlockHeader header, Transaction currentTx, int index)
            {
                throw new InvalidTransactionException(header, $"Transaction {currentTx.Hash} at index {index} failed with error {result.ErrorDescription}", result);
            }

            /// <summary>
            /// Used by <see cref="FilterManager"/> through <see cref="IMainProcessingContext"/>
            /// </summary>
            public interface ITransactionProcessedEventHandler
            {
                void OnTransactionProcessed(TxProcessedEventArgs txProcessedEventArgs);
            }
        }
    }
}
