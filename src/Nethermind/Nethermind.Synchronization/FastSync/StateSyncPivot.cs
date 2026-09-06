// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using ConcurrentCollections;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Synchronization.FastSync
{
    public class StateSyncPivot(IBlockTree blockTree, ISyncConfig syncConfig, ILogManager? logManager) : IStateSyncPivot
    {
        private BlockHeader? _bestHeader;
        private readonly ILogger _logger = logManager?.GetClassLogger<StateSyncPivot>() ?? throw new ArgumentNullException(nameof(logManager));

        public ulong Diff => (blockTree.BestSuggestedHeader?.Number ?? 0UL).SaturatingSub(_bestHeader?.Number ?? 0UL);

        public BlockHeader? GetPivotHeader()
        {
            ulong target = (blockTree.BestSuggestedHeader?.Number ?? 0UL) + syncConfig.StateMinDistanceFromHead;
            ulong best = _bestHeader?.Number ?? 0UL;
            if (_bestHeader is null || (target > best && target - best >= syncConfig.StateMaxDistanceFromHead))
            {
                TrySetNewBestHeader($"distance from HEAD:{Diff}");
            }
            else if (!blockTree.IsMainChain(_bestHeader))
            {
                TrySetNewBestHeader("pivot reorged out");
            }

            return _bestHeader;
        }

        public void UpdateHeaderForcefully()
        {
            ulong target = (blockTree.BestSuggestedHeader?.Number ?? 0UL) + syncConfig.StateMinDistanceFromHead;
            if (_bestHeader is null || target > _bestHeader.Number)
            {
                TrySetNewBestHeader("too many empty responses");
            }
        }

        public void UpdateHeaderAfterFailureStreak()
        {
            if (_bestHeader is null)
            {
                TrySetNewBestHeader("too many empty responses");
                return;
            }

            // Moving the pivot changes the state root that every in-flight and queued range was requested at, so
            // each one comes back unusable. When the move is a response to a streak of unusable responses that cost
            // is only worth paying if the new pivot is far enough from the old one to be a genuinely different
            // target. `UpdateHeaderForcefully` moves whenever the pivot is at or behind the head - i.e. always. On
            // a slow chain that is mostly harmless because the head rarely moves between two streaks and the pivot
            // is re-set to the block it already had, but on a fast chain the head has advanced by the time the next
            // streak lands, so every move invalidates the very work that would have ended the streak and
            // manufactures the next one. Measured on OP Mainnet (2 s blocks) against 2.0.0-rc: 74 469 forced
            // updates in 60 h, one every 2.9 s, average step 1.5 blocks, `State Ranges (Phase 1)` pinned at 0.00 %
            // and zero accounts ever committed, while the natural `distance from HEAD` re-pivot never once fired.
            //
            // Requiring the same minimum step the natural path already treats as "meaningfully behind" bounds the
            // rate of these moves by chain progress rather than by failure rate.
            ulong head = blockTree.BestSuggestedHeader?.Number ?? 0UL;
            ulong best = _bestHeader.Number;
            if (head <= best || head - best < syncConfig.StateMinDistanceFromHead)
            {
                Metrics.ForcedStatePivotUpdatesSuppressed++;
                return;
            }

            Metrics.ForcedStatePivotUpdates++;
            TrySetNewBestHeader("too many empty responses");
        }

        private void TrySetNewBestHeader(string msg)
        {
            BlockHeader bestSuggestedHeader = blockTree.BestSuggestedHeader; // Note: Best suggested header is always `syncConfig.StateMinDistanceFromHead`. behind from actual head.
            ulong targetBlockNumber = bestSuggestedHeader?.Number ?? 0UL;
            // The new pivot must be at least one block after the sync pivot as the forward downloader does not
            // download the block at the sync pivot which may cause state not found error if state was downloaded
            // at exactly sync pivot.
            targetBlockNumber = syncConfig.StaticSnapPivot
                ? blockTree.SyncPivot.BlockNumber
                : Math.Max(targetBlockNumber, blockTree.SyncPivot.BlockNumber + 1UL);

            BlockHeader bestHeader = blockTree.FindHeader(targetBlockNumber);

            if (bestHeader is null)
            {
                if (syncConfig.StaticSnapPivot && _logger.IsDebug) _logger.Debug($"Snap - {msg} - Pivot header {targetBlockNumber} not yet available in the block tree; waiting for fast headers.");
                return;
            }

            if (bestHeader.Hash == _bestHeader?.Hash) return;

            if (_logger.IsDebug) _logger.Debug($"Snap - {msg} - Pivot changed from {_bestHeader?.Number} to {bestHeader.Number}");
            _bestHeader = bestHeader;
        }

        public ConcurrentHashSet<Hash256> UpdatedStorages { get; } = [];

        public bool CanFinalize(BlockHeader pivot) => true;
    }
}
