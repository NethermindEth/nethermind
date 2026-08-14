// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockProductionTransactionsExecutor(
            ITransactionProcessorAdapter transactionProcessor,
            IWorldState stateProvider,
            IBlockProductionTransactionPicker txPicker,
            ILogManager logManager,
            IBlockAccessListManager balManager,
            ITxPool txPool)
            : IBlockProductionTransactionsExecutor
        {
            private readonly ILogger _logger = logManager.GetClassLogger<BlockProductionTransactionsExecutor>();

            protected EventHandler<TxProcessedEventArgs>? _transactionProcessed;

            event EventHandler<AddingTxEventArgs>? IBlockProductionTransactionsExecutor.AddingTransaction
            {
                add => txPicker.AddingTransaction += value;
                remove => txPicker.AddingTransaction -= value;
            }

            public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            {
                transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
                balManager.SetBlockExecutionContext(blockExecutionContext);
            }

            public virtual TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions,
                BlockReceiptsTracer receiptsTracer, CancellationToken token = default)
            {
                balManager.NextTransaction();

                // We start with high number as don't want to resize too much
                const int defaultTxCount = 512;

                BlockToProduce? blockToProduce = block as BlockToProduce;

                // Don't use blockToProduce.Transactions.Count() as that would fully enumerate which is expensive
                int txCount = blockToProduce is not null ? defaultTxCount : block.Transactions.Length;
                IEnumerable<Transaction> transactions = blockToProduce?.Transactions ?? block.Transactions;

                using ArrayPoolListRef<Transaction> includedTx = new(txCount);

                HashSet<Transaction> consideredTx = new(ByHashTxComparer.Instance);
                int i = 0;
                foreach (Transaction currentTx in transactions)
                {
                    // Check if we have gone over time or the payload has been requested
                    if (token.IsCancellationRequested) break;

                    TxAction action = ProcessTransaction(block, currentTx, i++, receiptsTracer, processingOptions, consideredTx);
                    if (action == TxAction.Stop) break;

                    consideredTx.Add(currentTx);
                    if (action == TxAction.Add)
                    {
                        includedTx.Add(currentTx);
                        if (blockToProduce is not null)
                        {
                            blockToProduce.TxByteLength += currentTx.GetLength(false);
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
                    ITransactionProcessorAdapter processor = balManager.Enabled ? balManager.GetTxProcessor() : transactionProcessor;
                    TransactionResult result = processor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);

                    if (result)
                    {
                        _transactionProcessed?.Invoke(this,
                            new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));
                        balManager.NextTransaction();
                    }
                    else
                    {
                        balManager.Rollback();
                        args.Set(TxAction.Skip, result.ErrorDescription!);
                        EvictUnpaidFrameTx(currentTx, result);
                    }
                }

                return args.Action;

                [MethodImpl(MethodImplOptions.NoInlining)]
                void DebugSkipReason(Transaction currentTx, AddingTxEventArgs args)
                    => _logger.Debug($"Skipping transaction {currentTx.ToShortString()} because: {args.Reason}.");
            }

            /// <summary>
            /// Evicts an EIP-8141 frame transaction whose frames did not approve payment, so the producer runs
            /// its validation prefix at most once per time the transaction is pooled.
            /// </summary>
            /// <remarks>
            /// A frame transaction only charges a fee once a frame approves payment. One whose prefix does not
            /// approve leaves the producer with the whole prefix burned and nothing collected, and because
            /// nothing else evicts it, every later block repeats that work.
            /// Only <see cref="TransactionResult.ErrorType.MalformedTransaction"/> is evicted: every other error
            /// either clears on its own (a nonce gap, a base fee above the transaction's cap) or is already
            /// handled by the pool's own eviction passes. A structurally invalid prefix (bad signature, approval
            /// scope on an atomic batch, a gas budget that overflows) never reaches here — static validation
            /// rejects it at admission. What reaches here failed on chain state (an unfunded payer, a VERIFY
            /// frame reading storage), so <see cref="ITxPool.EvictTransaction"/> clears the long-term cache and
            /// the transaction can re-enter once that state changes.
            /// </remarks>
            private void EvictUnpaidFrameTx(Transaction tx, in TransactionResult result)
            {
                if (!tx.SupportsFrames || result.Error != TransactionResult.ErrorType.MalformedTransaction) return;

                if (txPool.EvictTransaction(tx) && _logger.IsDebug)
                    _logger.Debug($"Evicted frame transaction {tx.ToShortString()} from the pool: {result.ErrorDescription}.");
            }
        }
    }
}
