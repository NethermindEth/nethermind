// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockProductionTransactionsExecutor(
            ITransactionProcessor txProcessor,
            IBlockProductionTransactionPicker txPicker,
            ILogManager logManager)
            : IBlockProductionTransactionsExecutor
        {
            public BlockProductionTransactionsExecutor(
                ITransactionProcessor transactionProcessor,
                ISpecProvider specProvider,
                ILogManager logManager) : this(transactionProcessor,
                new BlockProductionTransactionPicker(specProvider), logManager)
            {
            }

            private readonly ITransactionProcessorAdapter _transactionProcessor = new BuildUpTransactionProcessorAdapter(txProcessor);
            private readonly ILogger _logger = logManager.GetClassLogger();

            protected EventHandler<TxProcessedEventArgs>? _transactionProcessed;

            event EventHandler<TxProcessedEventArgs>? IBlockProcessor.IBlockTransactionsExecutor.TransactionProcessed
            {
                add => _transactionProcessed += value;
                remove => _transactionProcessed -= value;
            }

            event EventHandler<AddingTxEventArgs>? IBlockProductionTransactionsExecutor.AddingTransaction
            {
                add => txPicker.AddingTransaction += value;
                remove => txPicker.AddingTransaction -= value;
            }

            public virtual TxReceipt[] ProcessTransactions(IWorldState worldState, Block block,
                ProcessingOptions processingOptions,
                BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
            {
                IEnumerable<Transaction> transactions = GetTransactions(block);

                int i = 0;
                LinkedHashSet<Transaction> transactionsInBlock = new(ByHashTxComparer.Instance);
                BlockExecutionContext blkCtx = new(block.Header);
                foreach (Transaction currentTx in transactions)
                {
                    TxAction action = ProcessTransaction(worldState, block, in blkCtx, currentTx, i++, receiptsTracer, processingOptions, transactionsInBlock);
                    if (action == TxAction.Stop) break;
                }

                worldState.Commit(spec, receiptsTracer);

                SetTransactions(block, transactionsInBlock);
                return receiptsTracer.TxReceipts.ToArray();
            }

            protected TxAction ProcessTransaction(IWorldState worldState, Block block,
                in BlockExecutionContext blkCtx,
                Transaction currentTx,
                int index,
                BlockReceiptsTracer receiptsTracer,
                ProcessingOptions processingOptions,
                LinkedHashSet<Transaction> transactionsInBlock,
                bool addToBlock = true)
            {
                AddingTxEventArgs args = txPicker.CanAddTransaction(block, currentTx, transactionsInBlock, worldState);

                if (args.Action != TxAction.Add)
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping transaction {currentTx.ToShortString()} because: {args.Reason}.");
                }
                else
                {
                    TransactionResult result = _transactionProcessor.ProcessTransaction(in blkCtx, currentTx, receiptsTracer, processingOptions, worldState);

                    if (result)
                    {
                        if (addToBlock)
                        {
                            transactionsInBlock.Add(currentTx);
                            _transactionProcessed?.Invoke(this,
                                new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
                        }
                    }
                    else
                    {
                        args.Set(TxAction.Skip, result.Error!);
                    }
                }

                return args.Action;
            }

            protected static IEnumerable<Transaction> GetTransactions(Block block) => block.GetTransactions();

            protected static void SetTransactions(Block block, IEnumerable<Transaction> transactionsInBlock)
            {
                block.TrySetTransactions(transactionsInBlock.ToArray());
            }
        }
    }
}
