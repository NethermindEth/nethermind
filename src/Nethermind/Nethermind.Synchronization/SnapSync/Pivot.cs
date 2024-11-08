// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    internal class Pivot
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

        public Pivot(IBlockTree blockTree, ISyncConfig syncConfig, ILogManager logManager)
        {
            _blockTree = blockTree;
            _syncConfig = syncConfig;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public BlockHeader GetPivotHeader()
        {
            if (_bestHeader is null || _blockTree.BestSuggestedHeader?.Number - _bestHeader.Number >= _syncConfig.StateMaxDistanceFromHead)
            {
                LogPivotChanged($"distance from HEAD:{Diff}");

                long targetBlockNumber = Math.Max(_blockTree.BestSuggestedHeader.Number - _syncConfig.StateMaxDistanceFromHead, 0);
                BlockHeader bestHeader = _blockTree.FindHeader(targetBlockNumber);
                if (bestHeader != null)
                {
                    _bestHeader = bestHeader;
                }
            }

            if (_logger.IsDebug)
            {
                var currentHeader = _blockTree.FindHeader(_bestHeader.Number);
                if (currentHeader.StateRoot != _bestHeader.StateRoot)
                {
                    _logger.Warn($"SNAP - Pivot:{_bestHeader.StateRoot}, Current:{currentHeader.StateRoot}");
                }
            }

            return _bestHeader;
        }

        private void LogPivotChanged(string msg)
        {
            _logger.Info($"Snap - {msg} - Pivot changed from {_bestHeader?.Number} to {_blockTree.BestSuggestedHeader?.Number}");
        }

        public void UpdateHeaderForcefully()
        {
            if (_blockTree.BestSuggestedHeader?.Number > _bestHeader.Number)
            {
                LogPivotChanged("too many empty responses");
                _bestHeader = _blockTree.BestSuggestedHeader;
            }
        }
    }
}
