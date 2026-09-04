// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Metric;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Shadow-mode BAL bulk applier: recomputes the post-block state root by applying the suggested
/// BAL's final per-account/per-slot values on an isolated copy of the parent state and compares
/// it with the canonical post-block root. Diagnostics only, gated by
/// <c>IBlocksConfig.ParallelBalStateRootShadow</c> (default off); never affects consensus results.
/// </summary>
public partial class BlockAccessListManager
{
    private IReadOnlyTxProcessorSource? _shadowRootEnv;
    // Serializes background shadow runs: _shadowRootEnv is a single reusable env, and back-to-back
    // blocks could otherwise overlap on it.
    private readonly Lock _shadowRootLock = new();

    /// <summary>
    /// When shadow mode is enabled on the parallel BAL path, queues a background recomputation of
    /// the state root from the suggested BAL on a parent-state env, compared with
    /// <see cref="Block.StateRoot"/>.
    /// </summary>
    /// <remarks>
    /// Runs off the block-processing thread so a shadow-enabled node does not add state-root and GC
    /// work to <c>newPayload</c> latency — the same nodes report the <c>BalApplyTime</c> /
    /// <c>BalStateRootTime</c> / <c>BalRootReadyLagTime</c> baselines this PR introduces. The gates
    /// and the parent root are captured synchronously; the queued work never throws.
    /// </remarks>
    private void QueueShadowStateRootComparison(Block block)
    {
        if (!blocksConfig.ParallelBalStateRootShadow || !ParallelExecutionEnabled)
        {
            return;
        }

        Hash256? parentStateRoot = _parentStateRoot;
        // Captured here rather than read in the work item: the block's fields are reset as soon as
        // processing completes, which can happen before the queued comparison runs.
        IReleaseSpec? spec = _blockSpec;
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.self.RunShadowStateRootComparisonCore(state.block, state.parentStateRoot, state.spec),
            (self: this, block, parentStateRoot, spec),
            preferLocal: false);
    }

    /// <summary>Synchronous shadow comparison for the current block; internal for tests.</summary>
    internal void RunShadowStateRootComparison(Block block)
    {
        if (!blocksConfig.ParallelBalStateRootShadow || !ParallelExecutionEnabled)
        {
            return;
        }

        RunShadowStateRootComparisonCore(block, _parentStateRoot, _blockSpec);
    }

    /// <summary>
    /// Never throws: completed comparisons bump <c>Metrics.BalShadowRootComparisons</c> (the soak's
    /// positive signal — "0 mismatches" is meaningless while this stays 0), mismatches bump
    /// <c>Metrics.BalShadowRootMismatches</c>, and unexpected failures bump
    /// <c>Metrics.BalShadowRootFailures</c> and are swallowed so the canonical pipeline is unaffected.
    /// </summary>
    private void RunShadowStateRootComparisonCore(Block block, Hash256? parentStateRoot, IReleaseSpec? spec)
    {
        ReadOnlyBlockAccessList? bal = block.BlockAccessList;
        Hash256? canonicalRoot = block.StateRoot;
        if (bal is null || parentStateRoot is null || canonicalRoot is null || spec is null)
        {
            return;
        }

        if (readOnlyTxProcessingEnvFactory is null)
        {
            if (_logger.IsDebug) _logger.Debug("BAL shadow state root skipped: no read-only tx processing env factory available.");
            return;
        }

        try
        {
            Hash256 shadowRoot;
            lock (_shadowRootLock)
            {
                _shadowRootEnv ??= readOnlyTxProcessingEnvFactory.Create();
                using IReadOnlyTxProcessingScope scope = _shadowRootEnv.Build(CreateParentStateHeader(block, parentStateRoot));
                shadowRoot = ComputeShadowStateRoot(bal, scope.WorldState, spec);
            }

            Evm.Metrics.IncrementBalShadowRootComparisons();
            if (shadowRoot != canonicalRoot)
            {
                long mismatches = Evm.Metrics.IncrementBalShadowRootMismatches();
                if (ShouldLogShadowIssue(mismatches) && _logger.IsError) _logger.Error($"BAL shadow state root mismatch for block {block.Number} ({block.Hash}): shadow {shadowRoot}, canonical {canonicalRoot}.");
            }
        }
        catch (Exception ex)
        {
            // Shadow mode is diagnostics only: report and continue, never fail the block.
            long failures = Evm.Metrics.IncrementBalShadowRootFailures();
            if (ShouldLogShadowIssue(failures) && _logger.IsError) _logger.Error($"BAL shadow state root computation failed for block {block.Number} ({block.Hash}).", ex);
        }
    }

    /// <summary>First occurrences always log, then sampled — the counters carry the totals, and a
    /// sustained per-block issue (e.g. peer-fed blocks with bogus header roots) must not flood the
    /// error log.</summary>
    private static bool ShouldLogShadowIssue(long count) => count <= 10 || (count & 127) == 0;

    /// <summary>
    /// Bulk-applies the BAL's post-block values (account fields via <see cref="BalPostState"/>,
    /// last value per changed storage slot, code inserts) onto <paramref name="shadowState"/>
    /// anchored at the parent state, then commits and returns the recalculated state root.
    /// </summary>
    private static Hash256 ComputeShadowStateRoot(ReadOnlyBlockAccessList bal, IWorldState shadowState, IReleaseSpec spec)
    {
        foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
        {
            Address address = accountChanges.Address;
            Account? parent = shadowState.TryGetAccount(address, out AccountStruct parentStruct)
                ? new Account(parentStruct.Nonce, parentStruct.Balance, new Hash256(parentStruct.StorageRoot), new Hash256(parentStruct.CodeHash))
                : null;

            Account? post = BalPostState.Compute(parent, accountChanges, spec);
            if (post is null)
            {
                // Absent post-block (EIP-158 empty or never materialized); storage goes with the account.
                if (parent is not null)
                {
                    shadowState.DeleteAccount(address);
                }
                continue;
            }

            if (!ReferenceEquals(post, parent))
            {
                shadowState.CreateAccountIfNotExists(address, 0, 0);

                UInt256 currentBalance = shadowState.GetBalance(address);
                if (post.Balance > currentBalance)
                {
                    shadowState.AddToBalance(address, post.Balance - currentBalance, spec);
                }
                else if (post.Balance < currentBalance)
                {
                    shadowState.SubtractFromBalance(address, currentBalance - post.Balance, spec);
                }

                if (shadowState.GetNonce(address) != post.Nonce)
                {
                    shadowState.SetNonce(address, post.Nonce);
                }

                if (accountChanges.CodeChanges.Length > 0)
                {
                    shadowState.InsertCode(address, accountChanges.CodeChanges[^1].Code, spec);
                }
            }

            foreach (ReadOnlySlotChanges slotChange in accountChanges.StorageChanges)
            {
                if (slotChange.Changes.Length > 0)
                {
                    // StorageChange.Value is EvmWord (Vector256<byte>) in big-endian wire form.
                    EvmWord value = slotChange.Changes[^1].Value;
                    ReadOnlySpan<byte> valueBytes = MemoryMarshal.CreateReadOnlySpan(
                        ref Unsafe.As<EvmWord, byte>(ref value), 32);
                    shadowState.Set(new StorageCell(address, slotChange.Key), [.. valueBytes.WithoutLeadingZeros()]);
                }
            }
        }

        shadowState.Commit(spec);
        shadowState.RecalculateStateRoot();
        return shadowState.StateRoot;
    }

    // Timing sinks for the BAL apply pipeline, mirroring BlockProcessor's sink pattern:
    // IsEnabled folds to ExecutionMetricsFlag so the Stopwatch calls vanish when metrics are off.
    private readonly struct BalWarmupWaitTimeSink : IMetricSink
    {
        public static void AddTicks(long ticks) => Evm.Metrics.IncrementBalWarmupWaitTime(ticks);
        public static bool IsEnabled => ExecutionMetricsFlag.IsActive;
    }

    private readonly struct BalApplyTimeSink : IMetricSink
    {
        public static void AddTicks(long ticks) => Evm.Metrics.IncrementBalApplyTime(ticks);
        public static bool IsEnabled => ExecutionMetricsFlag.IsActive;
    }

    private readonly struct BalStateRootTimeSink : IMetricSink
    {
        public static void AddTicks(long ticks) => Evm.Metrics.IncrementBalStateRootTime(ticks);
        public static bool IsEnabled => ExecutionMetricsFlag.IsActive;
    }
}
