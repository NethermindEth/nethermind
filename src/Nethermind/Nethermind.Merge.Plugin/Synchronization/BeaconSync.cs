// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin.Synchronization
{
    public class BeaconSync : IMergeSyncController, IBeaconSyncStrategy
    {
        private readonly IBeaconPivot _beaconPivot;
        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly IBlockCacheService _blockCacheService;
        private bool _isInBeaconModeControl = false;
        private readonly ILogger _logger;

        public BeaconSync(
            IBeaconPivot beaconPivot,
            IBlockTree blockTree,
            ISyncConfig syncConfig,
            IBlockCacheService blockCacheService,
            ILogManager logManager)
        {
            _beaconPivot = beaconPivot;
            _blockTree = blockTree;
            _syncConfig = syncConfig;
            _blockCacheService = blockCacheService;
            _logger = logManager.GetClassLogger();
        }

        public void StopSyncing()
        {
            if (!_isInBeaconModeControl)
            {
                _beaconPivot.RemoveBeaconPivot();
                _blockCacheService.BlockCache.Clear();
            }

            _isInBeaconModeControl = true;
        }

        public void InitBeaconHeaderSync(BlockHeader blockHeader)
        {
            StopBeaconModeControl();
            _beaconPivot.EnsurePivot(blockHeader);
        }

        public void StopBeaconModeControl()
        {
            _isInBeaconModeControl = false;
        }

        public bool ShouldBeInBeaconHeaders()
        {
            bool beaconPivotExists = _beaconPivot.BeaconPivotExists();
            bool notInBeaconModeControl = !_isInBeaconModeControl;
            bool notFinishedBeaconHeaderSync = !IsBeaconSyncHeadersFinished();

            if (_logger.IsTrace) _logger.Trace($"ShouldBeInBeaconHeaders: NotInBeaconModeControl: {notInBeaconModeControl}, BeaconPivotExists: {beaconPivotExists}, NotFinishedBeaconHeaderSync: {notFinishedBeaconHeaderSync} LowestInsertedBeaconHeaderNumber: {_blockTree.LowestInsertedBeaconHeader?.Number}, BeaconPivot: {_beaconPivot.PivotNumber}, BeaconPivotDestinationNumber: {_beaconPivot.PivotDestinationNumber}");
            return beaconPivotExists &&
                   notInBeaconModeControl &&
                   notFinishedBeaconHeaderSync;
        }

        public bool ShouldBeInBeaconModeControl() => _isInBeaconModeControl;

        public bool IsBeaconSyncHeadersFinished()
        {
            BlockHeader? lowestInsertedBeaconHeader = _blockTree.LowestInsertedBeaconHeader;
            bool chainMerged =
                ((lowestInsertedBeaconHeader?.Number ?? 0) - 1) <= (_blockTree.BestSuggestedHeader?.Number ?? long.MaxValue) &&
                lowestInsertedBeaconHeader is not null &&
                _blockTree.IsKnownBlock(lowestInsertedBeaconHeader.Number - 1, lowestInsertedBeaconHeader.ParentHash!);
            bool finished = lowestInsertedBeaconHeader is null
                            || lowestInsertedBeaconHeader.Number <= _beaconPivot.PivotDestinationNumber
                            || (!_syncConfig.StrictMode && chainMerged);

            if (_logger.IsTrace) _logger.Trace(
                $"IsBeaconSyncHeadersFinished: {finished}," +
                $" BeaconPivotExists: {_beaconPivot.BeaconPivotExists()}," +
                $" LowestInsertedBeaconHeaderHash: {_blockTree.LowestInsertedBeaconHeader?.Hash}," +
                $" LowestInsertedBeaconHeaderNumber: {_blockTree.LowestInsertedBeaconHeader?.Number}," +
                $" BestSuggestedHeader: {_blockTree.BestSuggestedHeader?.Number}," +
                $" ChainMerged: {chainMerged}," +
                $" StrictMode: {_syncConfig.StrictMode}," +
                $" BeaconPivot: {_beaconPivot.PivotNumber}," +
                $" BeaconPivotDestinationNumber: {_beaconPivot.PivotDestinationNumber}");
            return finished;
        }

        // At this point, beacon headers sync is finished and has found an ancestor that exists in the block tree
        // beacon sync moves forward from the ancestor and is finished when the block body gap is filled + processed
        // in the case of fast sync, this is the gap between the state sync head with beacon block head
        /// <summary>
        /// Tells if <see cref="blockHeader"/>
        /// </summary>
        /// <param name="blockHeader"></param>
        /// <returns></returns>
        public bool IsBeaconSyncFinished(BlockHeader? blockHeader) => !_beaconPivot.BeaconPivotExists() || (blockHeader is not null && _blockTree.WasProcessed(blockHeader.Number, blockHeader.GetOrCalculateHash()));

        public long? GetTargetBlockHeight()
        {
            if (_beaconPivot.BeaconPivotExists())
            {
                return _beaconPivot.ProcessDestination?.Number ?? _beaconPivot.PivotNumber;
            }
            return null;
        }
    }

    public interface IMergeSyncController
    {
        void StopSyncing();

        void InitBeaconHeaderSync(BlockHeader blockHeader);

        void StopBeaconModeControl();
    }
}
