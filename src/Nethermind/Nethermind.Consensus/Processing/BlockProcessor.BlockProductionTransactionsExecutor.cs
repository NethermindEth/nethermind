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
                HashSet<AddressAsKey>? unpaidFrameTxSenders = null;
                int i = 0;
                foreach (Transaction currentTx in transactions)
                {
                    // Check if we have gone over time or the payload has been requested
                    if (token.IsCancellationRequested) break;

                    TxAction action = ProcessTransaction(block, currentTx, i++, receiptsTracer, processingOptions, consideredTx, ref unpaidFrameTxSenders);
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
                HashSet<Transaction> transactionsInBlock,
                ref HashSet<AddressAsKey>? unpaidFrameTxSenders)
            {
                // VERIFY may read only tx.sender's own state, so once one of a sender's frame transactions fails
                // validation in this build, that state has moved since the pool simulated it and the sender's other
                // frame transactions would likely fail too, each burning up to MAX_VERIFY_GAS unpaid. They are not
                // evicted: a later head may make them valid again.
                if (currentTx.SupportsFrames && unpaidFrameTxSenders?.Contains(currentTx.SenderAddress!) == true)
                {
                    if (_logger.IsDebug)
                        DebugSkipReason(currentTx, new AddingTxEventArgs(transactionsInBlock.Count, currentTx, block, transactionsInBlock)
                            .Set(TxAction.Skip, "Sender's earlier frame transaction in this block failed validation"));
                    return TxAction.Skip;
                }

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
                        if (IsUnpaidFrameTx(currentTx, result))
                        {
                            (unpaidFrameTxSenders ??= []).Add(currentTx.SenderAddress!);
                            EvictUnpaidFrameTx(currentTx, result);
                        }
                    }
                }

                return args.Action;

                [MethodImpl(MethodImplOptions.NoInlining)]
                void DebugSkipReason(Transaction currentTx, AddingTxEventArgs args)
                    => _logger.Debug($"Skipping transaction {currentTx.ToShortString()} because: {args.Reason}.");
            }

            /// <summary>Evicts a frame transaction whose frames approved no payment; nothing else evicts it, so every
            /// later block would re-burn its validation prefix for nothing.</summary>
            /// <remarks>Only <see cref="TransactionResult.ErrorType.MalformedTransaction"/> qualifies. Some of those
            /// reasons turn on head state (an out-of-range recent-root reference, a SENDER frame reached before an
            /// approval) and could become valid on a later head, so eviction here is a drop, not a permanent verdict:
            /// the sender must resubmit if the transaction becomes valid again.</remarks>
            private void EvictUnpaidFrameTx(Transaction tx, in TransactionResult result)
            {
                if (txPool.EvictTransaction(tx) && _logger.IsDebug)
                    _logger.Debug($"Evicted frame transaction {tx.ToShortString()} from the pool: {result.ErrorDescription}.");
            }

            private static bool IsUnpaidFrameTx(Transaction tx, in TransactionResult result) =>
                tx.SupportsFrames && result.Error == TransactionResult.ErrorType.MalformedTransaction;
        }
    }
}
