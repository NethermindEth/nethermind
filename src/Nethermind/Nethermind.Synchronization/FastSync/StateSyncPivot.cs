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
            if (bestHeader is not null)
            {
                if (_bestHeader?.Number != bestHeader.Number)
                {
                    if (_logger.IsInfo) _logger.Info($"Snap - {msg} - Pivot changed from {_bestHeader?.Number} to {bestHeader.Number}");
                }
                else if (_logger.IsDebug)
                {
                    _logger.Debug($"Snap - {msg} - pivot kept at {bestHeader.Number}");
                }

                _bestHeader = bestHeader;
            }
            else if (syncConfig.StaticSnapPivot && _logger.IsDebug)
            {
                _logger.Debug($"Snap - static pivot header {targetBlockNumber} not yet available in the block tree; waiting for fast headers.");
            }
        }

        public ConcurrentHashSet<Hash256> UpdatedStorages { get; } = [];

        public bool CanFinalize(BlockHeader pivot) => true;
    }
}
