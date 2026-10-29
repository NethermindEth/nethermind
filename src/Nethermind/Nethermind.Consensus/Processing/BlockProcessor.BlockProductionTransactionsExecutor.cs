// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
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
            private IReleaseSpec? _spec;

            // Items added to the end-of-block reserve after a build on _learnedItemReserveParent overshot the block access
            // list item limit, for end-of-block work the reserve does not model (e.g. a withdrawal contract instead of
            // balance credits). Scoped to that parent so one unusual block does not shrink the budget of later ones.
            // Builds of one producer env are serialized, so the fields need no synchronization.
            private long _learnedItemReserve;
            private Hash256? _learnedItemReserveParent;

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
                _spec = blockExecutionContext.Spec;
            }

            public void ReportItemLimitExceeded(long excessItems)
            {
                if (excessItems <= 0) return;
                _learnedItemReserve += excessItems;
                if (_logger.IsWarn) _logger.Warn($"Produced block exceeded the block access list item limit by {excessItems} items; reserving {_learnedItemReserve} more items in later builds on parent {_learnedItemReserveParent}.");
            }

            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions,
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
                BlockAccessListItemBudget itemBudget = CreateItemBudget(block);
                foreach (Transaction currentTx in transactions)
                {
                    // Check if we have gone over time or the payload has been requested
                    if (token.IsCancellationRequested) break;

                    TxAction action = ProcessTransaction(block, currentTx, includedTx.Count, receiptsTracer, processingOptions, consideredTx, ref itemBudget);
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

            /// <summary>
            /// EIP-7928: the items transactions may add to the block access list, so that the end-of-block steps still fit
            /// under <c>block_gas_limit / ITEM_COST</c>. Disabled when no block access list is built.
            /// </summary>
            private BlockAccessListItemBudget CreateItemBudget(Block block)
            {
                if (!balManager.Enabled || _spec is null) return default;

                if (block.Header.ParentHash != _learnedItemReserveParent)
                {
                    _learnedItemReserveParent = block.Header.ParentHash;
                    _learnedItemReserve = 0;
                }

                BlockAccessListItemSet blockAccessList = balManager.BlockAccessListItems;
                long limit = (long)(block.Header.GasLimit / Eip7928Constants.ItemCost);
                long reserve = BlockAccessListItemReserve.Calculate(block, _spec, stateProvider, blockAccessList)
                    + _learnedItemReserve;
                // Only the pre-execution system calls are merged at this point, so the count is cheap.
                return new BlockAccessListItemBudget(limit - reserve - blockAccessList.ItemCount);
            }

            private TxAction ProcessTransaction(
                Block block,
                Transaction currentTx,
                int index,
                BlockReceiptsTracer receiptsTracer,
                ProcessingOptions processingOptions,
                HashSet<Transaction> transactionsInBlock,
                ref BlockAccessListItemBudget itemBudget)
            {
                AddingTxEventArgs args = txPicker.CanAddTransaction(
                    block,
                    currentTx,
                    transactionsInBlock,
                    stateProvider,
                    receiptsTracer.CumulativeExecutionGasUsed,
                    receiptsTracer.BlockStateGasUsed);

                if (args.Action != TxAction.Add)
                {
                    if (_logger.IsDebug) DebugSkipReason(currentTx, args);
                }
                else
                {
                    ITransactionProcessorAdapter processor = balManager.Enabled ? balManager.GetTxProcessor() : transactionProcessor;
                    Snapshot snapshot = itemBudget.Enabled ? stateProvider.TakeSnapshot() : Snapshot.Empty;
                    int receiptsSnapshot = receiptsTracer.TakeSnapshot();
                    TransactionResult result = processor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);

                    if (result && itemBudget.Enabled && !itemBudget.TryConsume(balManager.CountPendingTransactionItems()))
                    {
                        // Leave the executed transaction out: undo its state, its slice and its receipt.
                        stateProvider.Restore(snapshot);
                        balManager.Rollback();
                        receiptsTracer.DiscardLastTx();
                        args.Set(TxAction.Skip, "Block access list item limit reached");
                        if (_logger.IsDebug) DebugSkipReason(currentTx, args);
                    }
                    else if (result)
                    {
                        _transactionProcessed?.Invoke(this,
                            new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));
                        balManager.NextTransaction();
                    }
                    else
                    {
                        // Drop any receipt/header gas the failed tx left behind. The processor's own running block
                        // gas is not reset here, but the next receipt rewrites the header's gas used from receipts kept.
                        receiptsTracer.Restore(receiptsSnapshot);
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

            /// <summary>Block access list items left for transactions; a disabled budget admits everything.</summary>
            private struct BlockAccessListItemBudget(long remaining)
            {
                public readonly bool Enabled = true;
                private long _remaining = remaining;

                public bool TryConsume(int items)
                {
                    if (items > _remaining) return false;
                    _remaining -= items;
                    return true;
                }
            }

            /// <summary>Evicts a frame transaction whose frames approved no payment; nothing else evicts it, so every
            /// later block would re-burn its validation prefix for nothing.</summary>
            /// <remarks>Only <see cref="TransactionResult.ErrorType.MalformedTransaction"/> qualifies. Some of those
            /// reasons turn on head state (an out-of-range recent-root reference, a SENDER frame reached before an
            /// approval) and could become valid on a later head, so eviction here is a drop, not a permanent verdict:
            /// the sender must resubmit if the transaction becomes valid again.</remarks>
            private void EvictUnpaidFrameTx(Transaction tx, in TransactionResult result)
            {
                if (!tx.SupportsFrames || result.Error != TransactionResult.ErrorType.MalformedTransaction) return;

                if (txPool.EvictTransaction(tx) && _logger.IsDebug)
                    _logger.Debug($"Evicted frame transaction {tx.ToShortString()} from the pool: {result.ErrorDescription}.");
            }
        }
    }
}
