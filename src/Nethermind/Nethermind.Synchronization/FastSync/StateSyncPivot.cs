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

namespace Nethermind.Synchronization.FastSync
{
    public class StateSyncPivot
    {
        private readonly IBlockTree _blockTree;
        private BlockHeader _bestHeader;
        private readonly ILogger _logger;
        private readonly ISyncConfig _syncConfig;

        public long Diff
        {
            get
            {
                return (_blockTree.BestSuggestedHeader?.Number ?? 0) - (_bestHeader?.Number ?? 0);
            }
        }

        public StateSyncPivot(IBlockTree blockTree, ISyncConfig syncConfig, ILogManager logManager)
        {
            _blockTree = blockTree;
            _syncConfig = syncConfig;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public BlockHeader GetPivotHeader()
        {
            if (_bestHeader is null || _blockTree.BestSuggestedHeader?.Number - _bestHeader.Number >= _syncConfig.StateMaxDistanceFromHead)
            {
                TrySetNewBestHeader($"distance from HEAD:{Diff}");
            }

            if (_logger.IsDebug)
            {
                if (_bestHeader is not null)
                {
                    var currentHeader = _blockTree.FindHeader(_bestHeader.Number);
                    if (currentHeader.StateRoot != _bestHeader.StateRoot)
                    {
                        _logger.Warn($"SNAP - Pivot:{_bestHeader.StateRoot}, Current:{currentHeader.StateRoot}");
                    }
                }
            }

            return _bestHeader;
        }

        public void UpdateHeaderForcefully()
        {
            if (_blockTree.BestSuggestedHeader?.Number > _bestHeader.Number)
            {
                TrySetNewBestHeader("too many empty responses");
            }
        }

        private void TrySetNewBestHeader(string msg)
        {
            BlockHeader bestSuggestedHeader = _blockTree.BestSuggestedHeader;
            long targetBlockNumber = Math.Max(bestSuggestedHeader.Number - _syncConfig.StateMinDistanceFromHead, 0);
            BlockHeader bestHeader = _blockTree.FindHeader(targetBlockNumber);
            if (bestHeader is not null)
            {
                if (_logger.IsInfo) _logger.Info($"Snap - {msg} - Pivot changed from {_bestHeader?.Number} to {bestHeader.Number}");
                _bestHeader = bestHeader;
            }
        }

        public ConcurrentHashSet<Hash256> UpdatedStorages { get; } = new ConcurrentHashSet<Hash256>();
    }
}
