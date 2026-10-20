// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Synchronization;

/// <summary>Reconstructs finalized catch-up state, falling back to execution when commitments cannot be reproduced.</summary>
/// <remarks>
/// The consensus client must report finality ahead of the downloaded blocks. Engine API payloads always execute;
/// old executed blocks are counted in <see cref="Metrics.BalCatchUpFallbacks"/>. Missing retained lists also force execution.
/// </remarks>
public sealed class FinalizedBlockAccessListProcessor(
    IBlockProcessor inner,
    FinalizedBlockAccessListPolicy policy,
    IWorldState state,
    IReceiptStorage receiptStorage,
    IReceiptsRecovery receiptsRecovery,
    ITimestamper timestamper,
    IBlocksConfig blocksConfig,
    ILogManager logManager) : IBlockProcessor
{
    /// <summary>
    /// A block older than this many slots is being caught up rather than followed: the network finalizes a block
    /// about two epochs after its slot, so a block this old would normally be finalized already.
    /// </summary>
    public const ulong CatchUpAgeSlots = 64;

    private readonly ILogger _logger = logManager.GetClassLogger<FinalizedBlockAccessListProcessor>();
    private readonly ulong _catchUpAgeSeconds = CatchUpAgeSlots * Math.Max(blocksConfig.SecondsPerSlot, 1);
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
            | ProcessingOptions.NoValidation | ProcessingOptions.ForceSequentialBlockAccessList;
        if ((options & excluded) != 0 || blockTracer != NullBlockTracer.Instance)
            return inner.ProcessOne(block, options, blockTracer, spec, token);

        // The engine API validates the payloads it is handed, so those always execute.
        bool engineApi = options.ContainsFlag(ProcessingOptions.MarkAsProcessed);
        if (engineApi || options.ContainsFlag(ProcessingOptions.DoNotUpdateHead))
        {
            if (engineApi && IsCatchingUp(block.Header))
            {
                BalCatchUpEligibility eligibility = policy.Evaluate(block.Header);
                if (eligibility == BalCatchUpEligibility.Eligible) CountCatchUpExecution(block, BalCatchUpFallbackReason.EngineApi);
                else if (eligibility == BalCatchUpEligibility.NotFinalized) CountCatchUpExecution(block, BalCatchUpFallbackReason.NotFinalized);
            }
            return inner.ProcessOne(block, options, blockTracer, spec, token);
        }

        BalCatchUpEligibility verdict = policy.Evaluate(block.Header);
        if (verdict != BalCatchUpEligibility.Eligible)
        {
            if (verdict == BalCatchUpEligibility.NotFinalized && IsCatchingUp(block.Header))
                CountCatchUpExecution(block, BalCatchUpFallbackReason.NotFinalized);
            return inner.ProcessOne(block, options, blockTracer, spec, token);
        }

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
        if (_logger.IsDebug) _logger.Debug($"Reconstructed finalized {block.ToString(Block.Format.Short)} from its block access list");
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

    private bool IsCatchingUp(BlockHeader header) => timestamper.UnixTime.Seconds > header.Timestamp + _catchUpAgeSeconds;

    private void CountCatchUpExecution(Block block, BalCatchUpFallbackReason reason)
    {
        CountFallback(reason);
        if (_logger.IsDebug) _logger.Debug($"Executing catch-up {block.ToString(Block.Format.Short)}: {reason}");
    }
}
