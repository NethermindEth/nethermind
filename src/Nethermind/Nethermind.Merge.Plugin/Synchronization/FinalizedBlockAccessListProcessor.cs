// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Synchronization;

/// <summary>Reconstructs finalized catch-up state, falling back to execution when commitments cannot be reproduced.</summary>
public sealed class FinalizedBlockAccessListProcessor(
    IBlockProcessor inner,
    FinalizedBlockAccessListPolicy policy,
    IWorldState state,
    IReceiptStorage receiptStorage,
    IReceiptsRecovery receiptsRecovery,
    ILogManager logManager) : IBlockProcessor
{
    private readonly ILogger _logger = logManager.GetClassLogger<FinalizedBlockAccessListProcessor>();
    private event Action? _transactionsExecuted;

    /// <summary>The processor that executes blocks this decorator does not reconstruct.</summary>
    public IBlockProcessor Inner => inner;

    /// <inheritdoc/>
    public event Action? TransactionsExecuted
    {
        add { inner.TransactionsExecuted += value; _transactionsExecuted += value; }
        remove { inner.TransactionsExecuted -= value; _transactionsExecuted -= value; }
    }

    /// <inheritdoc/>
    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block block, ProcessingOptions options,
        IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
    {
        const ProcessingOptions excluded = ProcessingOptions.ReadOnlyChain | ProcessingOptions.ForceProcessing
            | ProcessingOptions.NoValidation | ProcessingOptions.DoNotUpdateHead | ProcessingOptions.ForceSequentialBlockAccessList;
        if ((options & excluded) != 0 || blockTracer != NullBlockTracer.Instance || !policy.CanReconstruct(block.Header))
            return inner.ProcessOne(block, options, blockTracer, spec, token);

        // Queued processing attaches a stored list only when it matches the header's commitment.
        if (block.BlockAccessList is not { } list) return Execute(BalCatchUpFallbackReason.NoBlockAccessList);

        // Applying the list skips the post-execution item limit check, so a list over the limit is executed instead.
        if (Eip7928Constants.ExceedsItemLimit(list.ItemCount, block.GasLimit)) return Execute(BalCatchUpFallbackReason.ItemLimitExceeded);

        TxReceipt[] receipts = [];
        if (block.Transactions.Length > 0)
        {
            // The downloader stores receipts only after checking them against the receipts root.
            if (!receiptStorage.HasBlock(block.Number, block.Hash!)) return Execute(BalCatchUpFallbackReason.NoReceipts);
            receipts = receiptStorage.Get(block);
            // The downloader stored them before senders were recovered, and a cached copy is served as stored.
            receiptsRecovery.TryRecover(block, receipts, forceRecoverSender: true);
        }

        // No transactions run, so background work such as prewarming can stop before the writes are applied.
        _transactionsExecuted?.Invoke();
        token.ThrowIfCancellationRequested();
        state.Commit(spec);
        state.ApplyBal(list);
        state.RecalculateStateRoot();
        if (state.StateRoot != block.StateRoot)
        {
            CountFallback(BalCatchUpFallbackReason.StateRootMismatch);
            throw new BlockProcessor.BlockAccessListSequentialRetryException(block.Header,
                $"BAL reconstruction mismatched state root for {block}; retrying execution.");
        }

        block.AccountChanges = list.GetStateChangedAddresses();
        Metrics.BalCatchUpBlocks++;
        return (block, receipts);

        (Block, TxReceipt[]) Execute(BalCatchUpFallbackReason reason)
        {
            CountFallback(reason);
            if (_logger.IsDebug) _logger.Debug($"Executing finalized {block.ToString(Block.Format.Short)}: {reason}");
            return inner.ProcessOne(block, options, blockTracer, spec, token);
        }
    }

    private static void CountFallback(BalCatchUpFallbackReason reason) =>
        Metrics.BalCatchUpFallbacks.AddOrUpdate(reason, 1, static (_, count) => count + 1);
}
