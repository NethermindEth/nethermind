// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
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
        if (lastRoot == Hash256.Zero)
        {
            // Plugin's startup bootstrap couldn't run (head was null at init); fire it now.
            BlockHeader? header = e.Block.Header;
            if (header?.StateRoot is null) return;
            FireAndForget.Run(
                () => AnalyzeAsync(header, CancellationToken.None),
                _logger,
                "StateComposition: deferred bootstrap scan failed");
            return;
        }

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

            // FlatDb's BeginScope materialises the snapshot bundle for one block,
            // so a diff across two roots needs one scope per side or the off-side
            // nodes resolve as Unknown and the walker silently emits zero deltas.
            BlockHeader prevHeader = _blockTree.FindHeader(_stateHolder.IncrementalBlock, BlockTreeLookupOptions.RequireCanonical)
                ?? throw new MissingTrieNodeException("prev block header is no longer canonical", null, TreePath.Empty, prevRoot);

            using IReadOnlyTrieStore oldStore = _worldStateManager.CreateReadOnlyTrieStore();
            using IDisposable oldScope = BeginScopeOrThrowMissing(oldStore, prevHeader, prevRoot);
            using IReadOnlyTrieStore newStore = _worldStateManager.CreateReadOnlyTrieStore();
            using IDisposable newScope = BeginScopeOrThrowMissing(newStore, head.Header, head.Header.StateRoot);
            IScopedTrieStore oldResolver = oldStore.GetTrieStore(null);
            IScopedTrieStore newResolver = newStore.GetTrieStore(null);

            TrieDiff diff = _diffWalker.ComputeDiff(prevRoot, head.Header.StateRoot, oldResolver, newResolver);
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
            // prevRoot is no longer reachable — pruned out, or rolled past FlatDb's
            // bundle window (BeginScopeOrThrowMissing translates that case here).
            // Drop the baseline so OnNewHeadBlock stops dispatching, then rescan.
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
    /// FlatDb's <see cref="ITrieStore.BeginScope"/> raises a generic
    /// <see cref="InvalidOperationException"/> when the requested state's bundle
    /// has rolled past its retention window or was never gatherable. Translate
    /// at the call site to the same recovery channel the pruning store uses
    /// (<see cref="MissingTrieNodeException"/>) so a single catch handles both
    /// backends without matching exception messages by string.
    /// </summary>
    private static IDisposable BeginScopeOrThrowMissing(IReadOnlyTrieStore store, BlockHeader header, Hash256 root)
    {
        try
        {
            return store.BeginScope(header);
        }
        catch (InvalidOperationException ex)
        {
            throw new MissingTrieNodeException(
                $"BeginScope failed for block {header.Number}: {ex.Message}",
                address: null, TreePath.Empty, root, innerException: ex);
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

        FireAndForget.Run(
            async () =>
            {
                Result<StateCompositionStats> result =
                    await AnalyzeAsync(header, CancellationToken.None).ConfigureAwait(false);
                if (!result.IsSuccess && _logger.IsWarn)
                    _logger.Warn($"StateComposition: auto-rescan skipped: {result.Error}");
            },
            _logger,
            "StateComposition: auto-rescan failed");
    }

}
