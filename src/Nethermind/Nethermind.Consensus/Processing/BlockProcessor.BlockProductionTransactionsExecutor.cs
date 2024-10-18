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
            IWorldState stateProvider,
            IBlockProductionTransactionPicker txPicker,
            ILogManager logManager)
            : IBlockProductionTransactionsExecutor
        {
            private readonly ITransactionProcessorAdapter _transactionProcessor = new BuildUpTransactionProcessorAdapter(txProcessor);
            private readonly ILogger _logger = logManager.GetClassLogger();

            public BlockProductionTransactionsExecutor(
                IReadOnlyTxProcessingScope readOnlyTxProcessingEnv,
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
                IWorldState stateProvider,
                ISpecProvider specProvider,
                ILogManager logManager) : this(transactionProcessor, stateProvider,
                new BlockProductionTransactionPicker(specProvider), logManager)
            {
            }

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

            public virtual TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions,
                BlockReceiptsTracer receiptsTracer, IReleaseSpec spec)
            {
                Transaction?[] transactions = GetTransactions(block).DistinctBy((tx) => tx, ByHashTxComparer.Instance).ToArray();

                var groups = transactions.Select((t, i) => (t, i)).GroupBy(ti => ti.t.SenderAddress).Where(g => g.Count() > 0);
                LinkedHashSet<Transaction> transactionsInBlock = new(ByHashTxComparer.Instance);
                foreach (var group in groups)
                {
                    var order = group.OrderBy(ti => ti.t.Nonce).Select(ti => ti.t).ToArray();
                    var current = group.OrderBy(ti => ti.i).Select(ti => ti.i).ToArray();
                    bool blank = false;
                    for (int index = 0; index < current.Length; index++)
                    {
                        var offset = current[index];
                        Transaction? tx = null;
                        if (!blank)
                        {
                            tx = order[index];
                            if (txPicker.CanAddTransaction(block, tx, transactionsInBlock, stateProvider).Action
                                == TxAction.Invalid)
                            {
                                tx = null;
                                blank = true;
                            }
                        }
                        transactions[offset] = tx;
                    }
                }

                transactionsInBlock.Clear();
                transactions = transactions.Where(tx => tx is not null).ToArray();

                transactions.AsSpan().Sort((tx0, tx1) =>
                    tx0.SenderAddress == tx1.SenderAddress ?
                    0 : tx0.MaxPriorityFeePerGas.CompareTo(tx1.MaxPriorityFeePerGas)
                );

                int i = 0;
                BlockExecutionContext blkCtx = new(block.Header);
                foreach (Transaction currentTx in transactions)
                {
                    TxAction action = ProcessTransaction(block, in blkCtx, currentTx, i++, receiptsTracer, processingOptions, transactionsInBlock);
                    if (action == TxAction.Stop) break;
                }

                stateProvider.Commit(spec, receiptsTracer);

                SetTransactions(block, transactionsInBlock);
                return receiptsTracer.TxReceipts.ToArray();
            }

            protected TxAction ProcessTransaction(
                Block block,
                in BlockExecutionContext blkCtx,
                Transaction currentTx,
                int index,
                BlockReceiptsTracer receiptsTracer,
                ProcessingOptions processingOptions,
                LinkedHashSet<Transaction> transactionsInBlock,
                bool addToBlock = true)
            {
                AddingTxEventArgs args = txPicker.CanAddTransaction(block, currentTx, transactionsInBlock, stateProvider);

                if (args.Action != TxAction.Add)
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping transaction {currentTx.ToShortString()} because: {args.Reason}.");
                }
                else
                {
                    TransactionResult result = _transactionProcessor.ProcessTransaction(in blkCtx, currentTx, receiptsTracer, processingOptions, stateProvider);

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
