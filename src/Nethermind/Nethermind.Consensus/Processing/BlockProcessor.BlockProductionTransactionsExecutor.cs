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
        public class BlockProductionTransactionsExecutor : IBlockProductionTransactionsExecutor
        {
            private readonly ITransactionProcessorAdapter _transactionProcessor;
            private readonly IWorldState _stateProvider;
            private readonly IBlockProductionTransactionPicker _blockProductionTransactionPicker;
            private readonly ILogger _logger;
            private readonly ILogManager _logManager;

            public BlockProductionTransactionsExecutor(
                ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv,
                ISpecProvider specProvider,
                ILogManager logManager)
                : this(
                    readOnlyTxProcessingEnv.TransactionProcessor,
                    readOnlyTxProcessingEnv.StateProvider,
                    specProvider,
                    logManager)
            {
            }

            public BlockProductionTransactionsExecutor(
                ITransactionProcessor transactionProcessor,
                IWorldState stateProvider,
                ISpecProvider specProvider,
                ILogManager logManager) : this(transactionProcessor, stateProvider,
                new BlockProductionTransactionPicker(specProvider), logManager)
            {
            }

            public BlockProductionTransactionsExecutor(ITransactionProcessor txProcessor, IWorldState stateProvider,
                IBlockProductionTransactionPicker txPicker, ILogManager logManager)
            {
                _transactionProcessor = new BuildUpTransactionProcessorAdapter(txProcessor);
                _stateProvider = stateProvider;
                _blockProductionTransactionPicker = txPicker;
                _logger = logManager.GetClassLogger();
                _logManager = logManager;
            }

            private BlockProductionTransactionsExecutor(ITransactionProcessorAdapter txProcessorAdapter, IWorldState stateProvider,
                IBlockProductionTransactionPicker txPicker, ILogManager logManager)
            {
                _transactionProcessor = txProcessorAdapter;
                _stateProvider = stateProvider;
                _blockProductionTransactionPicker = txPicker;
                _logger = logManager.GetClassLogger();
                _logManager = logManager;
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

            public virtual TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions,
                BlockExecutionTracer executionTracer, IReleaseSpec spec)
            {
                IEnumerable<Transaction> transactions = GetTransactions(block);

                int i = 0;
                LinkedHashSet<Transaction> transactionsInBlock = new(ByHashTxComparer.Instance);
                BlockExecutionContext blkCtx = new(block.Header);
                foreach (Transaction currentTx in transactions)
                {
                    TxAction action = ProcessTransaction(block, in blkCtx, currentTx, i++, executionTracer, processingOptions, transactionsInBlock);
                    if (action == TxAction.Stop) break;
                }

                _stateProvider.Commit(spec, executionTracer);

                SetTransactions(block, transactionsInBlock);
                return executionTracer.TxReceipts.ToArray();
            }

            protected TxAction ProcessTransaction(
                Block block,
                in BlockExecutionContext blkCtx,
                Transaction currentTx,
                int index,
                BlockExecutionTracer executionTracer,
                ProcessingOptions processingOptions,
                LinkedHashSet<Transaction> transactionsInBlock,
                bool addToBlock = true)
            {
                AddingTxEventArgs args =
                    _blockProductionTransactionPicker.CanAddTransaction(block, currentTx, transactionsInBlock,
                        _stateProvider);

                if (args.Action != TxAction.Add)
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping transaction {currentTx.ToShortString()} because: {args.Reason}.");
                }
                else
                {
                    _transactionProcessor.ProcessTransaction(in blkCtx, currentTx, executionTracer, processingOptions, _stateProvider);

                    if (addToBlock)
                    {
                        transactionsInBlock.Add(currentTx);
                        _transactionProcessed?.Invoke(this,
                            new TxProcessedEventArgs(index, currentTx, executionTracer.TxReceipts[index]));
                    }
                }

                return args.Action;
            }

            public IBlockProcessor.IBlockTransactionsExecutor WithNewStateProvider(IWorldState worldState)
            {
                return new BlockProductionTransactionsExecutor(_transactionProcessor, worldState,
                    _blockProductionTransactionPicker, _logManager);
            }

            protected static IEnumerable<Transaction> GetTransactions(Block block) => block.GetTransactions();

            protected static void SetTransactions(Block block, IEnumerable<Transaction> transactionsInBlock)
            {
                block.TrySetTransactions(transactionsInBlock.ToArray());
            }
        }
    }
}
