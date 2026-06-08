// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using ConcurrentCollections;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Synchronization.FastSync
{
    public class StateSyncPivot(IBlockTree blockTree, ISyncConfig syncConfig, ILogManager? logManager) : IStateSyncPivot
    {
        private BlockHeader? _bestHeader;
        private readonly ILogger _logger = logManager?.GetClassLogger<StateSyncPivot>() ?? throw new ArgumentNullException(nameof(logManager));

        // BestSuggestedHeader.Number and _bestHeader.Number are ulong. Diff is long (can be negative during reorgs).
        public ulong Diff => blockTree.BestSuggestedHeader?.Number ?? 0UL - (_bestHeader?.Number ?? 0UL);

        public BlockHeader? GetPivotHeader()
        {
            // BestSuggestedHeader.Number is ulong; StateMinDistanceFromHead/StateMaxDistanceFromHead are long.
            // Cast to long for arithmetic; safe for realistic chain heights.
            if (_bestHeader is null || ((long)(blockTree.BestSuggestedHeader?.Number ?? 0UL) + syncConfig.StateMinDistanceFromHead) - (long)_bestHeader.Number >= syncConfig.StateMaxDistanceFromHead)
            {
                TrySetNewBestHeader($"distance from HEAD:{Diff}");
            }

            return _bestHeader;
        }

        public void UpdateHeaderForcefully()
        {
            if (_bestHeader is null || ((long)(blockTree.BestSuggestedHeader?.Number ?? 0UL) + syncConfig.StateMinDistanceFromHead) > (long)_bestHeader.Number)
            {
                TrySetNewBestHeader("too many empty responses");
            }
        }

        private void TrySetNewBestHeader(string msg)
        {
            BlockHeader bestSuggestedHeader = blockTree.BestSuggestedHeader; // Note: Best suggested header is always `syncConfig.StateMinDistanceFromHead`. behind from actual head.
            // bestSuggestedHeader.Number is ulong; target is long for Math.Max and FindHeader.
            ulong targetBlockNumber = bestSuggestedHeader?.Number ?? 0UL;
            targetBlockNumber = Math.Max(targetBlockNumber, 0);
            // The new pivot must be at least one block after the sync pivot as the forward downloader does not
            // download the block at the sync pivot which may cause state not found error if state was downloaded
            // at exactly sync pivot.
            // SyncPivot.BlockNumber is ulong; cast to long is safe for realistic chain heights.
            targetBlockNumber = Math.Max(targetBlockNumber, blockTree.SyncPivot.BlockNumber + 1);

            BlockHeader bestHeader = blockTree.FindHeader(targetBlockNumber);
            if (bestHeader is not null)
            {
                if (_logger.IsInfo) _logger.Info($"Snap - {msg} - Pivot changed from {_bestHeader?.Number} to {bestHeader.Number}");
                _bestHeader = bestHeader;
            }
        }

        public ConcurrentHashSet<Hash256> UpdatedStorages { get; } = [];

        public bool CanFinalize(BlockHeader pivot) => true;
    }
}
