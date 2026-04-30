// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Service;

internal sealed partial class StateCompositionService
{
    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        Hash256 lastRoot = _stateHolder.LastProcessedStateRoot;
        if (lastRoot == Hash256.Zero) return;

        Hash256? newRoot = e.Block.Header.StateRoot;
        if (newRoot is null || newRoot == lastRoot) return;

        _ = Task.Run(RunIncrementalDiff);
    }

    /// <summary>
    /// Execute a coalescing incremental diff against the latest head block.
    /// Non-blocking: if another diff is already running this call is dropped
    /// — the running one will pick up the latest head on its next iteration.
    /// </summary>
    internal void RunIncrementalDiff()
    {
        if (_shuttingDown) return;
        if (!_diffLock.TryEnter()) return;
        Hash256 prevRoot = Hash256.Zero;
        Block? head = null;
        try
        {
            // Re-read inside lock for coalescing: diff from real latest to current head.
            prevRoot = _stateHolder.LastProcessedStateRoot;
            head = _blockTree.Head;
            if (head?.Header.StateRoot is null || prevRoot == Hash256.Zero || head.Header.StateRoot == prevRoot) return;

            using IReadOnlyTrieStore readOnlyStore = _worldStateManager.CreateReadOnlyTrieStore();
            IScopedTrieStore resolver = readOnlyStore.GetTrieStore(null);

            TrieDiff diff = _diffWalker.ComputeDiff(prevRoot, head.Header.StateRoot, resolver);
            // Size-lookup lambda is invoked once per newly-observed code hash,
            // not per reference, so cost stays bounded by distinct new hashes.
            CumulativeTrieStats updated = _stateHolder.ApplyIncrementalDiffAndUpdate(
                diff, head.Number, head.Header.StateRoot,
                hash => _stateReader.GetCode(hash)?.Length ?? 0);

            Metrics.UpdateFromCumulativeStats(updated);
            // Skip the labeled-gauge republish when nothing changed; gauges retain their last value.
            if (_config.TrackDepthIncrementally && !diff.DepthDelta.IsEmpty())
                Metrics.UpdateDepthDistribution(_stateHolder.CurrentDepthStats);
            Metrics.StateCompIncrementalBlock = head.Number;
            Metrics.StateCompDiffsSinceBaseline = _stateHolder.DiffsSinceBaseline;
            Metrics.StateCompDiffsApplied++;

            if (_logger.IsDebug)
                _logger.Debug($"StateComposition: incremental diff applied at block {head.Number}, " +
                              $"accounts={updated.AccountsTotal}, slots={updated.StorageSlotsTotal}");
        }
        catch (MissingTrieNodeException ex)
        {
            // prevRoot is no longer in the trie DB (pruning window exceeded).
            // Invalidate so OnNewHeadBlock stops dispatching diffs, then rescan.
            Metrics.StateCompBaselineInvalidations++;
            _stateHolder.InvalidateBaseline();
            if (_logger.IsWarn)
            {
                string blockDesc = head?.Number.ToString() ?? "?";
                string prevDesc = prevRoot.ToString();
                _logger.Warn(
                    $"StateComposition: baseline root {prevDesc} missing from DB at block {blockDesc}; " +
                    $"invalidated baseline and scheduling a full rescan. Reason: {ex.Message}");
            }
            ScheduleBaselineRescan(head);
        }
        catch (Exception ex)
        {
            Metrics.StateCompDiffErrors++;
            if (_logger.IsError)
            {
                string blockDesc = head is not null ? head.Number.ToString() : "?";
                string prevDesc = prevRoot.ToString();
                string newDesc = head?.Header.StateRoot?.ToString() ?? "?";
                _logger.Error(
                    $"StateComposition: failed to compute incremental diff at block {blockDesc} " +
                    $"(prevRoot={prevDesc}, newRoot={newDesc})",
                    ex);
            }
        }
        finally
        {
            _diffLock.Exit();
        }
    }

    /// <summary>
    /// Fire-and-forget a full rescan after a stale-baseline detection. Runs
    /// outside <c>_diffLock</c> because the scan is long-running. <c>AnalyzeAsync</c>
    /// already serialises via <c>_scanLock</c> with fail-fast semantics, so
    /// back-to-back triggers collapse into one real scan.
    /// </summary>
    private void ScheduleBaselineRescan(Block? head)
    {
        BlockHeader? header = head?.Header ?? _blockTree.Head?.Header;
        if (header is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                Result<StateCompositionStats> result =
                    await AnalyzeAsync(header, CancellationToken.None).ConfigureAwait(false);

                if (!result.IsSuccess && _logger.IsWarn)
                    _logger.Warn($"StateComposition: auto-rescan skipped: {result.Error}");
            }
            catch (Exception ex)
            {
                // Guard the log call itself, not the catch: a `when (IsError)`
                // filter would let the exception escape into the unobserved-task
                // pipeline whenever Error logging is off.
                if (_logger.IsError)
                    _logger.Error("StateComposition: auto-rescan failed", ex);
            }
        });
    }

}
