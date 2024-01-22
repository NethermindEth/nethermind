// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Facade.Eth
{
    public class EthSyncingInfo : IEthSyncingInfo
    {
        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly ILogger _logger;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly ISyncProgressResolver _syncProgressResolver;

        public EthSyncingInfo(
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ISyncConfig syncConfig,
            ISyncModeSelector syncModeSelector,
            ISyncProgressResolver syncProgressResolver,
            ILogManager logManager)
        {
            _blockTree = blockTree;
            _receiptStorage = receiptStorage;
            _syncConfig = syncConfig;
            _syncModeSelector = syncModeSelector;
            _syncProgressResolver = syncProgressResolver;
            _logger = logManager.GetClassLogger();
        }

        public SyncingResult GetFullInfo()
        {
            long bestSuggestedNumber = _blockTree.FindBestSuggestedHeader()?.Number ?? 0;
            long headNumberOrZero = _blockTree.Head?.Number ?? 0;
            bool isSyncing = bestSuggestedNumber > headNumberOrZero + 8;
            SyncMode syncMode = _syncModeSelector.Current;

            if (_logger.IsTrace) _logger.Trace($"Start - EthSyncingInfo - BestSuggestedNumber: {bestSuggestedNumber}, HeadNumberOrZero: {headNumberOrZero}, IsSyncing: {isSyncing} {_syncConfig}. LowestInsertedBodyNumber: {_blockTree.LowestInsertedBodyNumber} LowestInsertedReceiptBlockNumber: {_receiptStorage.LowestInsertedReceiptBlockNumber}");
            if (isSyncing)
            {
                if (_logger.IsTrace) _logger.Trace($"Too far from head - EthSyncingInfo - HighestBlock: {bestSuggestedNumber}, CurrentBlock: {headNumberOrZero}");
                return ReturnSyncing(headNumberOrZero, bestSuggestedNumber, syncMode);
            }

            // If we're on FastSync mode and the pivot number is not defined (it's 0), then we might never need to download receipts/bodies
            // so we cannot check for the `LowestInsertedReceiptBlockNumber`.
            // On the other hand, if we do have a PivotNumber then we should download receipts/bodies, so we check if we're still downloading them.
            bool needsToDownloadReceiptsAndBodies = _syncConfig.PivotNumberParsed != 0;
            if (_syncConfig.FastSync && needsToDownloadReceiptsAndBodies)
            {
                if (_syncConfig.DownloadReceiptsInFastSync && !_syncProgressResolver.IsFastBlocksReceiptsFinished())
                {
                    if (_logger.IsTrace) _logger.Trace($"Receipts not finished - EthSyncingInfo - HighestBlock: {bestSuggestedNumber}, CurrentBlock: {headNumberOrZero}, AncientReceiptsBarrier: {_syncConfig.AncientReceiptsBarrierCalc}. LowestInsertedBodyNumber: {_blockTree.LowestInsertedBodyNumber} LowestInsertedReceiptBlockNumber: {_receiptStorage.LowestInsertedReceiptBlockNumber}");
                    return ReturnSyncing(headNumberOrZero, bestSuggestedNumber, syncMode);
                }

                if (_syncConfig.DownloadBodiesInFastSync && !_syncProgressResolver.IsFastBlocksBodiesFinished())
                {
                    if (_logger.IsTrace) _logger.Trace($"Bodies not finished - EthSyncingInfo - HighestBlock: {bestSuggestedNumber}, CurrentBlock: {headNumberOrZero}, AncientBodiesBarrier: {_syncConfig.AncientBodiesBarrierCalc}. LowestInsertedBodyNumber: {_blockTree.LowestInsertedBodyNumber} LowestInsertedReceiptBlockNumber: {_receiptStorage.LowestInsertedReceiptBlockNumber}");
                    return ReturnSyncing(headNumberOrZero, bestSuggestedNumber, syncMode);
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Node is not syncing - EthSyncingInfo - HighestBlock: {bestSuggestedNumber}, CurrentBlock: {headNumberOrZero}. LowestInsertedBodyNumber: {_blockTree.LowestInsertedBodyNumber} LowestInsertedReceiptBlockNumber: {_receiptStorage.LowestInsertedReceiptBlockNumber}");
            return SyncingResult.NotSyncing;
        }

        private static SyncingResult ReturnSyncing(long headNumberOrZero, long bestSuggestedNumber, SyncMode syncMode)
        {
            return new SyncingResult
            {
                CurrentBlock = headNumberOrZero,
                HighestBlock = bestSuggestedNumber,
                StartingBlock = 0L,
                SyncMode = syncMode,
                IsSyncing = true
            };
        }

        private readonly Stopwatch _syncStopwatch = new();
        public TimeSpan UpdateAndGetSyncTime()
        {
            if (!_syncStopwatch.IsRunning)
            {
                if (IsSyncing())
                {
                    _syncStopwatch.Start();
                }
                return TimeSpan.Zero;
            }

            if (!IsSyncing())
            {
                _syncStopwatch.Stop();
                return TimeSpan.Zero;
            }

            return _syncStopwatch.Elapsed;
        }

        public SyncMode SyncMode => _syncModeSelector.Current;

        public bool IsSyncing()
        {
            return GetFullInfo().IsSyncing;
        }
    }
}
