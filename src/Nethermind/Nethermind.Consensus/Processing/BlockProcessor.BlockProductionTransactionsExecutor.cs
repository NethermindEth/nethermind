// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
            ISpecProvider specProvider,
            IBlockProductionTransactionPicker txPicker,
            ILogManager logManager,
            IBlockAccessListManager balManager)
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
                IReleaseSpec spec = specProvider.GetSpec(block.Header);
                DepositRequestsLimiter depositRequestsLimiter = new(spec);

                using ArrayPoolListRef<Transaction> includedTx = new(txCount);

                HashSet<Transaction> consideredTx = new(ByHashTxComparer.Instance);
                int i = 0;
                foreach (Transaction currentTx in transactions)
                {
                    // Check if we have gone over time or the payload has been requested
                    if (token.IsCancellationRequested) break;

                    TxAction action = ProcessTransaction(block, currentTx, i++, receiptsTracer, processingOptions, consideredTx, ref depositRequestsLimiter);
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
                ref DepositRequestsLimiter depositRequestsLimiter)
            {
                AddingTxEventArgs args = txPicker.CanAddTransaction(block, currentTx, transactionsInBlock, stateProvider);

                if (args.Action != TxAction.Add)
                {
                    if (_logger.IsDebug) DebugSkipReason(currentTx, args);
                }
                else
                {
                    bool rollbackOnDepositRequestOverflow = depositRequestsLimiter.CanExceedCap(currentTx.GasLimit);
                    Snapshot stateSnapshot = rollbackOnDepositRequestOverflow ? stateProvider.TakeSnapshot() : default;
                    int receiptsCountBeforeTx = rollbackOnDepositRequestOverflow ? receiptsTracer.TakeSnapshot() : receiptsTracer.TxReceipts.Length;

                    ITransactionProcessorAdapter processor = balManager.Enabled ? balManager.GetTxProcessor() : transactionProcessor;
                    TransactionResult result = processor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);

                    if (result)
                    {
                        // Stub adapters in tests can return Ok without emitting a receipt; production adapters always do.
                        TxReceipt? txReceipt = receiptsTracer.TxReceipts.Length > receiptsCountBeforeTx ? receiptsTracer.LastReceipt : null;
                        if (depositRequestsLimiter.Enabled
                            && txReceipt?.Logs is { Length: > 0 } logs
                            && !depositRequestsLimiter.TryAdd(logs, out int depositRequestsInTx))
                        {
                            if (!rollbackOnDepositRequestOverflow)
                            {
                                ThrowDepositRequestCapInvariantViolation(currentTx, depositRequestsInTx);
                            }

                            stateProvider.Restore(stateSnapshot);
                            receiptsTracer.Restore(receiptsCountBeforeTx);
                            balManager.Rollback();
                            ResetGasUsed(currentTx);
                            if (_logger.IsDebug)
                            {
                                args.Set(TxAction.Skip, $"EIP-8254 deposit request cap reached: transaction has {depositRequestsInTx} deposit request(s)");
                                DebugSkipReason(currentTx, args);
                            }
                            return TxAction.Skip;
                        }

                        if (txReceipt is not null)
                        {
                            _transactionProcessed?.Invoke(this,
                                new TxProcessedEventArgs(index, currentTx, block.Header, txReceipt));
                        }
                        balManager.NextTransaction();
                    }
                    else
                    {
                        balManager.Rollback();
                        args.Set(TxAction.Skip, result.ErrorDescription!);
                    }
                }

                return args.Action;

                [MethodImpl(MethodImplOptions.NoInlining)]
                void DebugSkipReason(Transaction currentTx, AddingTxEventArgs args)
                    => _logger.Debug($"Skipping transaction {currentTx.ToShortString()} because: {args.Reason}.");
            }

            private static void ResetGasUsed(Transaction tx)
            {
                tx.SpentGas = 0;
                tx.BlockGasUsed = 0;
            }

            [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
            private static void ThrowDepositRequestCapInvariantViolation(Transaction tx, int depositRequestsInTx) =>
                throw new InvalidOperationException(
                    $"Transaction {tx.ToShortString()} exceeded the EIP-8254 deposit request cap without a rollback snapshot. Deposit requests in tx: {depositRequestsInTx}.");

            /// <summary>Per-block tally of EIP-6110 deposit requests included while building, gated by the EIP-8254 cap.</summary>
            private struct DepositRequestsLimiter(IReleaseSpec spec)
            {
                // 5x32 offsets + 5x32 lengths + ceil-32-pad(48+32+8+96+8) = 576 bytes (see ExecutionRequestsProcessor.DepositEventAbi).
                private const int DepositEventAbiEncodedDataSize = 576;

                // LOG-only floor (375 + 375 + 8*576 = 5358). Deposit contract burns more before each emit, so this
                // over-estimates max deposits/tx as a divisor in CanExceedCap. ThrowDepositRequestCapInvariantViolation
                // backstops any miscount.
                private const long MinDepositLogGas =
                    GasCostOf.Log + GasCostOf.LogTopic + GasCostOf.LogData * DepositEventAbiEncodedDataSize;
                private readonly Address? _depositContractAddress = spec.DepositContractAddress;
                private int _depositRequests;

                public readonly bool Enabled => _depositContractAddress is not null;

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public readonly bool CanExceedCap(long gasLimit) =>
                    Enabled
                    && (long)_depositRequests + gasLimit / MinDepositLogGas > Eip6110Constants.MaxDepositRequestsPerBlock;

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public bool TryAdd(LogEntry[] logs, out int depositRequestsInTx)
                {
                    depositRequestsInTx = CountDepositRequests(logs);
                    if (depositRequestsInTx == 0)
                    {
                        return true;
                    }

                    if (_depositRequests + depositRequestsInTx > Eip6110Constants.MaxDepositRequestsPerBlock)
                    {
                        return false;
                    }

                    _depositRequests += depositRequestsInTx;
                    return true;
                }

                private readonly int CountDepositRequests(LogEntry[] logs)
                {
                    Debug.Assert(_depositContractAddress is not null, "Reachable only via TryAdd, which is gated by Enabled.");
                    Address depositContract = _depositContractAddress;
                    int depositRequests = 0;
                    for (int i = 0; i < logs.Length; i++)
                    {
                        if (ExecutionRequestsProcessor.IsDepositLog(logs[i], depositContract))
                        {
                            depositRequests++;
                        }
                    }

                    return depositRequests;
                }
            }
        }
    }
}
