// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

#nullable enable

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
                ILogManager logManager,
                long maxTxLengthKilobytes = BlocksConfig.DefaultMaxTxKilobytes)
                : this(
                    readOnlyTxProcessingEnv.TransactionProcessor,
                    readOnlyTxProcessingEnv.WorldState,
                    specProvider,
                    logManager,
                    maxTxLengthKilobytes)
            {
            }

            public BlockProductionTransactionsExecutor(
                ITransactionProcessor transactionProcessor,
                IWorldState stateProvider,
                ISpecProvider specProvider,
                ILogManager logManager,
                long maxTxLengthKilobytes = BlocksConfig.DefaultMaxTxKilobytes) : this(transactionProcessor, stateProvider,
                new BlockProductionTransactionPicker(specProvider, maxTxLengthKilobytes), logManager)
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

            public virtual TxReceipt[] ProcessTransactions(Block block, in BlockExecutionContext blkCtx, ProcessingOptions processingOptions,
                BlockReceiptsTracer receiptsTracer, IReleaseSpec spec, CancellationToken token = default)
            {
                // We start with high number as don't want to resize too much
                const int defaultTxCount = 512;

                BlockToProduce? blockToProduce = block as BlockToProduce;

                // Don't use blockToProduce.Transactions.Count() as that would fully enumerate which is expensive
                int txCount = blockToProduce is not null ? defaultTxCount : block.Transactions.Length;
                IEnumerable<Transaction> transactions = blockToProduce?.Transactions ?? block.Transactions;

                using ArrayPoolList<Transaction> includedTx = new(txCount);

                HashSet<Transaction> consideredTx = new(ByHashTxComparer.Instance);
                int i = 0;
                foreach (Transaction currentTx in transactions)
                {
                    // Check if we have gone over time or the payload has been requested
                    if (token.IsCancellationRequested) break;

                    TxAction action = ProcessTransaction(block, in blkCtx, currentTx, i++, receiptsTracer, processingOptions, consideredTx);
                    if (action == TxAction.Stop) break;

                    consideredTx.Add(currentTx);
                    if (action == TxAction.Add)
                    {
                        includedTx.Add(currentTx);
                        if (blockToProduce is not null)
                        {
                            blockToProduce.TxByteLength += currentTx.GetLength();
                        }
                    }
                }

                block.Header.TxRoot = TxTrie.CalculateRoot(includedTx.AsSpan());
                if (blockToProduce is not null)
                {
                    blockToProduce.Transactions = includedTx.ToArray();
                }
                return receiptsTracer.TxReceipts.ToArray();
            }

            private TxAction ProcessTransaction(
                Block block,
                in BlockExecutionContext blkCtx,
                Transaction currentTx,
                int index,
                BlockReceiptsTracer receiptsTracer,
                ProcessingOptions processingOptions,
                HashSet<Transaction> transactionsInBlock)
            {
                AddingTxEventArgs args = txPicker.CanAddTransaction(block, currentTx, transactionsInBlock, stateProvider);

                if (args.Action != TxAction.Add)
                {
                    if (_logger.IsDebug) DebugSkipReason(currentTx, args);
                }
                else
                {
                    TransactionResult result = _transactionProcessor.ProcessTransaction(in blkCtx, currentTx, receiptsTracer, processingOptions, stateProvider);

                    if (result)
                    {
                        _transactionProcessed?.Invoke(this,
                            new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
                    }
                    else
                    {
                        args.Set(TxAction.Skip, result.Error!);
                    }
                }

                return args.Action;

                [MethodImpl(MethodImplOptions.NoInlining)]
                void DebugSkipReason(Transaction currentTx, AddingTxEventArgs args)
                    => _logger.Debug($"Skipping transaction {currentTx.ToShortString()} because: {args.Reason}.");
            }
        }
    }
}
