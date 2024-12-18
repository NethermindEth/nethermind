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
            if (_bestHeader is null || (blockTree.BestSuggestedHeader?.Number + MultiSyncModeSelector.FastSyncLag) - _bestHeader.Number >= syncConfig.StateMaxDistanceFromHead)
            {
                TrySetNewBestHeader($"distance from HEAD:{Diff}");
            }

            if (_logger.IsDebug)
            {
                if (_bestHeader is not null)
                {
                    BlockHeader? currentHeader = blockTree.FindHeader(_bestHeader.Number);
                    if (currentHeader?.StateRoot != _bestHeader.StateRoot)
                    {
                        _logger.Warn($"SNAP - Pivot:{_bestHeader.StateRoot}, Current:{currentHeader?.StateRoot}");
                    }
                }
            }

            return _bestHeader;
        }

        public void UpdateHeaderForcefully()
        {
            if ((blockTree.BestSuggestedHeader?.Number + MultiSyncModeSelector.FastSyncLag) > _bestHeader?.Number)
            {
                TrySetNewBestHeader("too many empty responses");
            }
        }

        private void TrySetNewBestHeader(string msg)
        {
            BlockHeader bestSuggestedHeader = blockTree.BestSuggestedHeader;
            long targetBlockNumber = Math.Max(bestSuggestedHeader?.Number ?? 0 + MultiSyncModeSelector.FastSyncLag - syncConfig.StateMinDistanceFromHead, 0);
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
