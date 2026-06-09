// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Taiko.TaikoSpec;
using Nethermind.Taiko.ZkGas;
using Nethermind.TxPool;

namespace Nethermind.Taiko.BlockTransactionExecutors;

public class BlockInvalidTxExecutor(ITransactionProcessorAdapter txProcessor, IWorldState worldState, ITxPool txPool, ILogManager logManager, ZkGasMeterHolder? zkGasMeterHolder = null, ISpecProvider? specProvider = null) : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly ILogger _logger = logManager.GetClassLogger<BlockInvalidTxExecutor>();

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;
    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        => txProcessor.SetBlockExecutionContext(in blockExecutionContext);

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        if (block.Transactions.Length == 0)
        {
            if (block.IsGenesis)
                return [];

            throw new ArgumentException("Block must contain at least the anchor transaction");
        }

        block.Transactions[0].IsAnchorTx = true;

        // ZK gas exclusion is only valid during block production. During validation the
        // block-level check in TaikoBlockProcessor is used instead, ensuring the validator
        // faithfully re-executes every transaction and produces the same GasUsed as the
        // producer. Gated on IsUnzenEnabled so the meter stays dormant pre-Unzen (matches
        // the validator-side gate in TaikoBlockProcessor.ProcessBlock).
        bool enforceZkGas = (processingOptions & ProcessingOptions.ProducingBlock) != 0
                            && zkGasMeterHolder is not null
                            && specProvider?.GetSpec(block.Header) is ITaikoReleaseSpec { IsUnzenEnabled: true };

        using ArrayPoolListRef<Transaction> correctTransactions = new(block.Transactions.Length);

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            bool isAnchor = i == 0;

            // Stop including transactions once the ZK gas block limit is already exceeded
            // (only during block production). The anchor (i == 0) is mandatory and must
            // always be included: the meter starts fresh so this branch cannot fire on
            // i == 0 in practice, but the explicit `!isAnchor` guard documents the
            // invariant and matches alethia-reth (`!is_anchor_transaction` filter in
            // crates/block/src/executor.rs).
            if (!isAnchor && enforceZkGas && zkGasMeterHolder!.Meter?.IsLimitExceeded == true)
                break;

            Snapshot snap = worldState.TakeSnapshot();
            // Also snapshot the receipts so that, if we roll back a transaction due to the
            // ZK gas limit, the pre-committed receipt (added by MarkAsSuccess inside
            // txProcessor.Execute) and the running Block.Header.GasUsed are both undone.
            int receiptsSnap = receiptsTracer.TakeSnapshot();
            Transaction tx = block.Transactions[i];

            if (tx.Type == TxType.Blob)
            {
                // Skip blob transactions
                continue;
            }

            using ITxTracer _ = receiptsTracer.StartNewTxTrace(tx);

            try
            {
                if (!txProcessor.Execute(tx, receiptsTracer))
                {
                    // if the transaction was invalid, we ignore it and continue.
                    // CancelTransaction clears IsLimitExceeded set by the intrinsic charge.
                    worldState.Restore(snap);
                    receiptsTracer.Restore(receiptsSnap);
                    if (enforceZkGas) zkGasMeterHolder!.Meter?.CancelTransaction();
                    continue;
                }
            }
            catch
            {
                // sometimes invalid transactions can throw exceptions because
                // they are detected later in the processing pipeline
                worldState.Restore(snap);
                receiptsTracer.Restore(receiptsSnap);
                if (enforceZkGas) zkGasMeterHolder!.Meter?.CancelTransaction();
                continue;
            }

            // If this transaction pushed the ZK gas over the block limit, exclude it
            // and stop building the block (matches alethia-reth payload builder behavior).
            // Only checked during production — see enforceZkGas above.
            //
            // The anchor (i == 0) is exempt from this exclusion: it is system-injected
            // and mandatory — TaikoBlockValidator rejects any block missing it. Dropping
            // it here would silently stall the chain. Instead, log a warning so the
            // misconfiguration (e.g. ZK gas multipliers or block-limit set too tight
            // relative to the anchor's cost) surfaces loudly. Subsequent transactions
            // will be skipped by the pre-loop guard at the next iteration. This mirrors
            // alethia-reth (`!is_anchor_transaction` clause in `try_execute_filtered`,
            // crates/block/src/executor.rs).
            if (enforceZkGas && zkGasMeterHolder!.Meter?.IsLimitExceeded == true)
            {
                if (isAnchor)
                {
                    if (_logger.IsWarn) _logger.Warn(
                        $"Anchor tx {tx.Hash} exceeded the ZK gas block limit. " +
                        "Anchor is mandatory and cannot be evicted; preserving it. " +
                        "Validators will reject this block via the block-level ZK gas check. " +
                        "This indicates a misconfiguration of ZkGasSchedule or the block ZK gas limit.");

                    // Keep the anchor: end its trace, fire the event, and add it to the
                    // committed transaction set just like a normal successful tx.
                    receiptsTracer.EndTxTrace();
                    TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(i, tx, block.Header, receiptsTracer.LastReceipt));
                    correctTransactions.Add(tx);
                    continue;
                }

                worldState.Restore(snap);
                // Roll back the receipt that MarkAsSuccess added during Execute so
                // that Block.Header.GasUsed does not include this transaction's gas.
                receiptsTracer.Restore(receiptsSnap);
                // Clear IsLimitExceeded so the flag does not leak into the block-level
                // check in TaikoBlockProcessor after production finishes.
                zkGasMeterHolder!.Meter!.CancelTransaction();
                // Remove the offending transaction from the tx pool so that the
                // proposer does not keep re-including it in future batches.
                // Without this, a tx that individually exceeds the ZK gas budget
                // causes infinite empty block production.
                if (txPool.RemoveTransaction(tx.Hash))
                {
                    if (_logger.IsInfo) _logger.Info($"Removed tx {tx.Hash} from pool: exceeded ZK gas limit");
                }
                break;
            }

            // only end the trace if the transaction was successful
            // so that we don't increment the receipt index for failed transactions
            receiptsTracer.EndTxTrace();
            TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(i, tx, block.Header, receiptsTracer.LastReceipt));
            correctTransactions.Add(tx);
        }

        block.TrySetTransactions([.. correctTransactions]);
        return [.. receiptsTracer.TxReceipts];
    }
}
