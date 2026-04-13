// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    private void RunIncrementalDiff()
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
            CumulativeSizeStats updated = _stateHolder.IncrementalStats!.Value.ApplyDiff(diff);
            _stateHolder.UpdateIncremental(updated, head.Number, head.Header.StateRoot, diff.DepthDelta);

            Metrics.UpdateFromCumulativeStats(updated);
            // Skip the 149-setter publish when the depth distribution did not change.
            // Gauges retain their last published value, which is correct — nothing changed.
            if (_config.TrackDepthIncrementally && diff.DepthDelta?.IsEmpty() != true)
                Metrics.UpdateFromDepthStats(_stateHolder.CurrentDepthStats);
            Metrics.StateCompIncrementalBlock = head.Number;
            Metrics.StateCompDiffsSinceBaseline = _stateHolder.DiffsSinceBaseline;
            Metrics.StateCompDiffsApplied++;

            MaybeWriteSnapshot(head, updated);

            if (_logger.IsDebug)
                _logger.Debug($"StateComposition: incremental diff applied at block {head.Number}, " +
                              $"accounts={updated.AccountsTotal}, slots={updated.StorageSlotsTotal}");
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
    /// Persist a snapshot at the configured interval and prune entries beyond the retention window.
    /// </summary>
    private void MaybeWriteSnapshot(Block head, CumulativeSizeStats updated)
    {
        if (!_config.PersistSnapshots || head.Number % _config.SnapshotInterval != 0) return;

        _snapshotStore.WriteSnapshot(new StateCompositionSnapshot(
            updated, head.Number, head.Header.StateRoot!,
            _stateHolder.DiffsSinceBaseline,
            _stateHolder.LastScanMetadata?.BlockNumber ?? 0,
            // CurrentDepthStats already returns a clone under lock.
            _stateHolder.CurrentDepthStats));

        // Prune stale snapshot entries beyond the configured retention window.
        int blocksToKeep = _config.SnapshotBlocksToKeep;
        if (blocksToKeep <= 0) return;

        long deleteAt = head.Number - blocksToKeep;
        if (deleteAt > 0)
            _snapshotStore.DeleteSnapshot(deleteAt);
    }
}
