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
using Nethermind.StateComposition.Diff;

namespace Nethermind.StateComposition.Service;

internal partial class StateCompositionService
{
    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        Hash256? lastRoot = _stateHolder.LastProcessedStateRoot;
        if (lastRoot is null) return; // No baseline yet

        Hash256? newRoot = e.Block.Header.StateRoot;
        if (newRoot is null || newRoot == lastRoot) return; // No state change

        _ = Task.Run(RunIncrementalDiff);
    }

    /// <summary>
    /// Execute a coalescing incremental diff against the latest head block.
    /// Non-blocking: if another diff is already running this call is dropped
    /// — the running one will pick up the latest head on its next iteration.
    /// </summary>
    internal void RunIncrementalDiff()
    {
        if (!_diffLock.TryEnter()) return;
        Hash256? prevRoot = null;
        Block? head = null;
        try
        {
            // Re-read inside lock for coalescing: diff from real latest to current head.
            prevRoot = _stateHolder.LastProcessedStateRoot;
            head = _blockTree.Head;
            if (head?.Header.StateRoot is null || head.Header.StateRoot == prevRoot) return;

            using IReadOnlyTrieStore readOnlyStore = _worldStateManager.CreateReadOnlyTrieStore();
            IScopedTrieStore resolver = readOnlyStore.GetTrieStore(null);
            TrieDiffWalker walker = new(resolver, _config.TrackDepthIncrementally);

            TrieDiff diff = walker.ComputeDiff(prevRoot, head.Header.StateRoot);
            // Feed the code-hash tracker: it looks up bytecode size exactly once
            // per newly-observed hash, so the cost is bounded by the number of
            // distinct code hashes introduced in this diff.
            CumulativeSizeStats updated = _stateHolder.ApplyIncrementalDiffAndUpdate(
                diff, head.Number, head.Header.StateRoot,
                hash => _stateReader.GetCode(hash)?.Length ?? 0);

            Metrics.UpdateFromCumulativeStats(updated);
            // Skip the labeled-gauge republish when the depth distribution did not change.
            // Gauges retain their last published value, which is correct — nothing changed.
            if (_config.TrackDepthIncrementally && diff.DepthDelta?.IsEmpty() != true)
                Metrics.UpdateDepthDistribution(_stateHolder.CurrentDepthStats);
            Metrics.StateCompIncrementalBlock = head.Number;
            Metrics.StateCompDiffsSinceBaseline = _stateHolder.DiffsSinceBaseline;
            Metrics.StateCompDiffsApplied++;

            MaybeWriteSnapshot(head, updated);

            if (_logger.IsDebug)
                _logger.Debug($"StateComposition: incremental diff applied at block {head.Number}, " +
                              $"accounts={updated.AccountsTotal}, slots={updated.StorageSlotsTotal}");
        }
        catch (MissingTrieNodeException ex)
        {
            // prevRoot is no longer in the trie DB (container stopped longer than
            // the pruning window, or a prune ran while the plugin was idle). The
            // snapshot baseline is unusable — clear it so OnNewHeadBlock's null
            // gate (line 20) silences every subsequent head block, then fire a
            // background rescan to reseed via InitializeIncremental.
            Metrics.StateCompBaselineInvalidations++;
            _stateHolder.InvalidateBaseline();
            if (_logger.IsWarn)
            {
                string blockDesc = head?.Number.ToString() ?? "?";
                string prevDesc = prevRoot?.ToString() ?? "?";
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
                string prevDesc = prevRoot?.ToString() ?? "?";
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
            catch (Exception ex) when (_logger.IsError)
            {
                _logger.Error("StateComposition: auto-rescan failed", ex);
            }
        });
    }

    /// <summary>
    /// Persist a snapshot at the configured interval and prune entries beyond the retention window.
    /// The holder captures every field under a single lock so the write cannot tear. Graceful
    /// shutdown force-flushes independently via <see cref="StopAsync"/>, so missing an interval
    /// only costs a few blocks of incremental replay after a crash.
    /// </summary>
    private void MaybeWriteSnapshot(Block head, CumulativeSizeStats updated)
    {
        if (!_config.PersistSnapshots || head.Number % _config.SnapshotInterval != 0) return;

        WriteSnapshotForHead(updated, head.Number, head.Header.StateRoot!);

        int blocksToKeep = _config.SnapshotBlocksToKeep;
        if (blocksToKeep <= 0) return;

        long deleteAt = head.Number - blocksToKeep;
        if (deleteAt > 0)
            _snapshotStore.DeleteSnapshot(deleteAt);
    }
}
