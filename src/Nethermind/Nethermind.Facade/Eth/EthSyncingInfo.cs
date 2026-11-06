// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;
using SyncMetrics = Nethermind.Synchronization.Metrics;

namespace Nethermind.Facade.Eth
{
    public class EthSyncingInfo(
        IBlockTree blockTree,
        ISyncPointers syncPointers,
        ISyncConfig syncConfig,
        ISyncModeSelector syncModeSelector,
        ISyncProgressResolver syncProgressResolver,
        IBeaconSyncStrategy beaconSyncStrategy,
        ILogManager logManager) : IEthSyncingInfo
    {
        public const int MaxDistanceForSynced = 8;
        private const ulong NoStartingBlock = ulong.MaxValue;

        private readonly IBlockTree _blockTree = blockTree;
        private readonly ISyncConfig _syncConfig = syncConfig;
        private readonly ILogger _logger = logManager.GetClassLogger<EthSyncingInfo>();
        private readonly ISyncPointers _syncPointers = syncPointers;
        private readonly ISyncModeSelector _syncModeSelector = syncModeSelector;
        private readonly ISyncProgressResolver _syncProgressResolver = syncProgressResolver;
        private readonly IBeaconSyncStrategy _beaconSyncStrategy = beaconSyncStrategy;
        private ulong _startingBlock = blockTree.Head?.Number ?? 0;

        /// <remarks>
        /// CL-driven catch-up keeps <c>Head</c> close to <c>BestSuggestedHeader</c> while the beacon sync target is far ahead,
        /// so the highest block is widened with <see cref="IBeaconSyncStrategy.GetTargetBlockHeight"/>.
        /// <c>BestSuggestedBeaconHeader</c> is not used: it is a high-water mark that an abandoned fork can leave above the head.
        /// </remarks>
        public SyncingResult GetFullInfo()
        {
            (bool isSyncing, ulong headNumberOrZero, ulong bestSuggestedNumber) = _blockTree.IsSyncing(maxDistanceForSynced: MaxDistanceForSynced);
            ulong beaconSyncTarget = _beaconSyncStrategy.GetTargetBlockHeight() ?? 0;
            if (beaconSyncTarget > bestSuggestedNumber)
            {
                bestSuggestedNumber = beaconSyncTarget;
                isSyncing |= bestSuggestedNumber > headNumberOrZero + (ulong)MaxDistanceForSynced;
            }
            SyncMode syncMode = _syncModeSelector.Current;

            if (_logger.IsTrace) _logger.Trace($"Start - EthSyncingInfo - BestSuggestedNumber: {bestSuggestedNumber}, HeadNumberOrZero: {headNumberOrZero}, IsSyncing: {isSyncing} {_syncConfig}. LowestInsertedBodyNumber: {_syncPointers.LowestInsertedBodyNumber} LowestInsertedReceiptBlockNumber: {_syncPointers.LowestInsertedReceiptBlockNumber}");
            if (isSyncing)
            {
                if (_logger.IsTrace) _logger.Trace($"Too far from head - EthSyncingInfo - HighestBlock: {bestSuggestedNumber}, CurrentBlock: {headNumberOrZero}");
                return ReturnSyncing(headNumberOrZero, bestSuggestedNumber, syncMode);
            }

            // If we're on FastSync mode and the pivot number is not defined (it's 0), then we might never need to download receipts/bodies
            // so we cannot check for the `LowestInsertedReceiptBlockNumber`.
            // On the other hand, if we do have a PivotNumber then we should download receipts/bodies, so we check if we're still downloading them.
            bool needsToDownloadReceiptsAndBodies = _blockTree.SyncPivot.BlockNumber != 0;
            if (_syncConfig.FastSync && needsToDownloadReceiptsAndBodies)
            {
                if (_syncConfig.DownloadReceiptsInFastSync && !_syncProgressResolver.IsFastBlocksReceiptsFinished())
                {
                    if (_logger.IsTrace) _logger.Trace($"Receipts not finished - EthSyncingInfo - HighestBlock: {bestSuggestedNumber}, CurrentBlock: {headNumberOrZero}, AncientReceiptsBarrier: {_syncConfig.AncientReceiptsBarrierCalc}. LowestInsertedBodyNumber: {_syncPointers.LowestInsertedBodyNumber} LowestInsertedReceiptBlockNumber: {_syncPointers.LowestInsertedReceiptBlockNumber}");
                    return ReturnSyncing(headNumberOrZero, bestSuggestedNumber, syncMode);
                }

                if (_syncConfig.DownloadBodiesInFastSync && !_syncProgressResolver.IsFastBlocksBodiesFinished())
                {
                    if (_logger.IsTrace) _logger.Trace($"Bodies not finished - EthSyncingInfo - HighestBlock: {bestSuggestedNumber}, CurrentBlock: {headNumberOrZero}, AncientBodiesBarrier: {_syncConfig.AncientBodiesBarrierCalc}. LowestInsertedBodyNumber: {_syncPointers.LowestInsertedBodyNumber} LowestInsertedReceiptBlockNumber: {_syncPointers.LowestInsertedReceiptBlockNumber}");
                    return ReturnSyncing(headNumberOrZero, bestSuggestedNumber, syncMode);
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Node is not syncing - EthSyncingInfo - HighestBlock: {bestSuggestedNumber}, CurrentBlock: {headNumberOrZero}. LowestInsertedBodyNumber: {_syncPointers.LowestInsertedBodyNumber} LowestInsertedReceiptBlockNumber: {_syncPointers.LowestInsertedReceiptBlockNumber}");
            Volatile.Write(ref _startingBlock, NoStartingBlock);
            return SyncingResult.NotSyncing;
        }

        /// <remarks>The head stays at genesis during snap sync, so the warp chunk fields carry the snap range progress.</remarks>
        private SyncingResult ReturnSyncing(ulong headNumberOrZero, ulong bestSuggestedNumber, SyncMode syncMode)
        {
            ulong startingBlock = Interlocked.CompareExchange(ref _startingBlock, headNumberOrZero, NoStartingBlock);
            SyncingResult result = new()
            {
                StartingBlock = Math.Min(startingBlock, headNumberOrZero),
                CurrentBlock = headNumberOrZero,
                HighestBlock = bestSuggestedNumber,
                SyncMode = syncMode,
                IsSyncing = true
            };

            if (_syncConfig.SnapSync && (syncMode & SyncMode.StateNodes) != 0)
            {
                result.SyncedAccounts = (ulong)SyncMetrics.SnapSyncedAccounts;
                result.SyncedStorage = (ulong)SyncMetrics.SnapSyncedStorageSlots;
                result.SyncedBytecodes = (ulong)SyncMetrics.SnapSyncedCodes;
                result.HealedTrienodes = (ulong)(SyncMetrics.SyncedStateTrieNodes + SyncMetrics.SyncedStorageTrieNodes);
                result.HealedBytecodes = (ulong)SyncMetrics.SyncedCodes;
                result.WarpChunksAmount = ProgressTracker.RangesProgressScale;
                result.WarpChunksProcessed = (ulong)Math.Clamp(SyncMetrics.SnapRangesProgress, 0, ProgressTracker.RangesProgressScale);
            }

            return result;
        }

        private readonly SyncTimeStopwatch _syncStopwatch = new();

        public TimeSpan UpdateAndGetSyncTime() => _syncStopwatch.UpdateAndGet(IsSyncing());

        public SyncMode SyncMode => _syncModeSelector.Current;

        public bool IsSyncing() => GetFullInfo().IsSyncing;
    }
}
