// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockProductionTransactionsExecutor : IBlockProductionTransactionsExecutor
        {
            private readonly ITransactionProcessorAdapter _transactionProcessor;
            private readonly IWorldState _worldState;
            private readonly BlockProductionTransactionPicker _blockProductionTransactionPicker;
            private readonly ILogger _logger;

            public BlockProductionTransactionsExecutor(
                ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv,
                ISpecProvider specProvider,
                ILogManager logManager)
                : this(
                    readOnlyTxProcessingEnv.TransactionProcessor,
                    readOnlyTxProcessingEnv.WorldState,
                    specProvider,
                    logManager)
            {
            }

            public BlockProductionTransactionsExecutor(
                ITransactionProcessor transactionProcessor,
                IWorldState worldState,
                ISpecProvider specProvider,
                ILogManager logManager)
            {
                _transactionProcessor = new BuildUpTransactionProcessorAdapter(transactionProcessor);
                _worldState = worldState;
                _blockProductionTransactionPicker = new BlockProductionTransactionPicker(specProvider);
                _logger = logManager.GetClassLogger();
            }

            protected EventHandler<TxProcessedEventArgs>? _transactionProcessed;
            event EventHandler<TxProcessedEventArgs>? IBlockProcessor.IBlockTransactionsExecutor.TransactionProcessed
            {
                add => _transactionProcessed += value;
                remove => _transactionProcessed -= value;
            }

            event EventHandler<AddingTxEventArgs>? IBlockProductionTransactionsExecutor.AddingTransaction
            {
                add => _blockProductionTransactionPicker.AddingTransaction += value;
                remove => _blockProductionTransactionPicker.AddingTransaction -= value;
            }

            public virtual TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
            {
                IEnumerable<Transaction> transactions = GetTransactions(block);

                int i = 0;
                LinkedHashSet<Transaction> transactionsInBlock = new(ByHashTxComparer.Instance);
                foreach (Transaction currentTx in transactions)
                {
                    TxAction action = ProcessTransaction(block, currentTx, i++, receiptsTracer, processingOptions, transactionsInBlock);
                    if (action == TxAction.Stop) break;
                }

                _worldState.Commit(spec, receiptsTracer);

                SetTransactions(block, transactionsInBlock);
                return receiptsTracer.TxReceipts.ToArray();
            }

            protected TxAction ProcessTransaction(
                Block block,
                Transaction currentTx,
                int index,
                BlockReceiptsTracer receiptsTracer,
                ProcessingOptions processingOptions,
                LinkedHashSet<Transaction> transactionsInBlock,
                bool addToBlock = true)
            {
                AddingTxEventArgs args = _blockProductionTransactionPicker.CanAddTransaction(block, currentTx, transactionsInBlock, _worldState);

                if (args.Action != TxAction.Add)
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping transaction {currentTx.ToShortString()} because: {args.Reason}.");
                }
                else
                {
                    _transactionProcessor.ProcessTransaction(block, currentTx, receiptsTracer, processingOptions, _worldState);

                    if (addToBlock)
                    {
                        transactionsInBlock.Add(currentTx);
                        _transactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
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
