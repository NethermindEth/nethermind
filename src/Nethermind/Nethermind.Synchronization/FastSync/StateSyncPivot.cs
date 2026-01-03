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

        public long Diff => checked((long)(blockTree.BestSuggestedHeader?.Number ?? 0UL)) - checked((long)(_bestHeader?.Number ?? 0UL));

        public BlockHeader? GetPivotHeader()
        {
            ulong bestSuggestedNumber = blockTree.BestSuggestedHeader?.Number ?? 0UL;
            if (
                _bestHeader is null ||
                (bestSuggestedNumber + (ulong)syncConfig.StateMinDistanceFromHead) - _bestHeader.Number >= (ulong)syncConfig.StateMaxDistanceFromHead)
            {
                TrySetNewBestHeader($"distance from HEAD:{Diff}");
            }

            return _bestHeader;
        }

        public void UpdateHeaderForcefully()
        {
            ulong bestSuggestedNumber = blockTree.BestSuggestedHeader?.Number ?? 0UL;
            if (_bestHeader is null || (bestSuggestedNumber + (ulong)syncConfig.StateMinDistanceFromHead) > _bestHeader.Number)
            {
                TrySetNewBestHeader("too many empty responses");
            }
        }

        private void TrySetNewBestHeader(string msg)
        {
            BlockHeader? bestSuggestedHeader = blockTree.BestSuggestedHeader; // Note: Best suggested header is always `syncConfig.StateMinDistanceFromHead`. behind from actual head.
            ulong targetBlockNumber = bestSuggestedHeader?.Number ?? 0UL;
            targetBlockNumber = Math.Max(targetBlockNumber, 0UL);
            // The new pivot must be at least one block after the sync pivot as the forward downloader does not
            // download the block at the sync pivot which may cause state not found error if state was downloaded
            // at exactly sync pivot.
            targetBlockNumber = Math.Max(targetBlockNumber, blockTree.SyncPivot.BlockNumber + 1);

            BlockHeader? bestHeader = blockTree.FindHeader(targetBlockNumber, BlockTreeLookupOptions.RequireCanonical);
            if (bestHeader is not null)
            {
                if (_logger.IsInfo) _logger.Info($"Snap - {msg} - Pivot changed from {_bestHeader?.Number} to {bestHeader.Number}");
                _bestHeader = bestHeader;
            }
        }

        public ConcurrentHashSet<Hash256> UpdatedStorages { get; } = new();
    }
}
