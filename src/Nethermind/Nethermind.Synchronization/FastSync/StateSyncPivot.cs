// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using ConcurrentCollections;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.FastSync
{
    public class StateSyncPivot(IBlockTree blockTree, ISyncConfig syncConfig, ILogManager? logManager)
    {
        private BlockHeader? _bestHeader;
        private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

        public long Diff => (blockTree.BestSuggestedHeader?.Number ?? 0) - (_bestHeader?.Number ?? 0);

        public BlockHeader? GetPivotHeader()
        {
            if (_bestHeader is null || (blockTree.BestSuggestedHeader?.Number + syncConfig.StateMinDistanceFromHead) - _bestHeader.Number >= syncConfig.StateMaxDistanceFromHead)
            {
                TrySetNewBestHeader($"distance from HEAD:{Diff}");
            }

            return _bestHeader;
        }

        public void UpdateHeaderForcefully()
        {
            if (_bestHeader is null || (blockTree.BestSuggestedHeader?.Number + syncConfig.StateMinDistanceFromHead) > _bestHeader.Number)
            {
                TrySetNewBestHeader("too many empty responses");
            }
        }

        private void TrySetNewBestHeader(string msg)
        {
            BlockHeader bestSuggestedHeader = blockTree.BestSuggestedHeader; // Note: Best suggested header is always `syncConfig.StateMinDistanceFromHead`. behind from actual head.
            long targetBlockNumber = (bestSuggestedHeader?.Number ?? 0);
            targetBlockNumber = Math.Max(targetBlockNumber, 0);
            // The new pivot must be at least one block after the sync pivot as the forward downloader does not
            // download the block at the sync pivot which may cause state not found error if state was downloaded
            // at exactly sync pivot.
            targetBlockNumber = Math.Max(targetBlockNumber, blockTree.SyncPivot.BlockNumber + 1);

            BlockHeader bestHeader = blockTree.FindHeader(targetBlockNumber);
            if (bestHeader is not null)
            {
                if (_logger.IsInfo) _logger.Info($"Snap - {msg} - Pivot changed from {_bestHeader?.Number} to {bestHeader.Number}");
                _bestHeader = bestHeader;
            }
        }

        public ConcurrentHashSet<Hash256> UpdatedStorages { get; } = new();
    }
}
