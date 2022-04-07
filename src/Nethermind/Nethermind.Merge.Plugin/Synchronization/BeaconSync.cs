//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin.Synchronization
{
    public class BeaconSync : IMergeSyncController, IBeaconSyncStrategy
    {
        private readonly IBeaconPivot _beaconPivot;
        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly IDb _metadataDb;
        private bool _isInBeaconModeControl = false;
        private bool _danglingChainMerged;
        private readonly ILogger _logger;

        public BeaconSync(
            IBeaconPivot beaconPivot,
            IBlockTree blockTree,
            ISyncConfig syncConfig,
            IDb metadataDb,
            ILogManager logManager)
        {
            _beaconPivot = beaconPivot;
            _blockTree = blockTree;
            _syncConfig = syncConfig;
            _metadataDb = metadataDb;
            _logger = logManager.GetClassLogger();

            Initialize();
        }

        private void Initialize()
        {
            LoadDanglingChainMerged();
        }

        private void LoadDanglingChainMerged()
        {
            DanglingChainMerged =
                _metadataDb.Get(MetadataDbKeys.DanglingChainMerged)?
                    .AsRlpValueContext().DecodeBool() ?? true;
        }

        public void SwitchToBeaconModeControl()
        {
            _isInBeaconModeControl = true;
        }

        public void InitSyncing(BlockHeader? blockHeader)
        {
            _isInBeaconModeControl = false;
            _beaconPivot.EnsurePivot(blockHeader);
        }

        public bool Enabled => true;

        public bool ShouldBeInBeaconHeaders()
        {
            bool beaconPivotExists =  _beaconPivot.BeaconPivotExists();
            bool notInBeaconModeControl = !_isInBeaconModeControl;
            bool notFinishedBeaconHeaderSync = !IsBeaconSyncHeadersFinished();

            return beaconPivotExists &&
                   notInBeaconModeControl &&
                   notFinishedBeaconHeaderSync;
        }

        public bool ShouldBeInBeaconModeControl() => _isInBeaconModeControl;
        
        public bool IsBeaconSyncHeadersFinished()
        {
            bool finished = _blockTree.LowestInsertedBeaconHeader == null
                            || _blockTree.LowestInsertedBeaconHeader?.Number == 0
                            || _blockTree.LowestInsertedBeaconHeader?.Number <= _syncConfig.PivotNumberParsed + 1;
            
            if (_logger.IsTrace) _logger.Trace($"IsBeaconSyncHeadersFinished: {finished}, BeaconPivotExists: {_beaconPivot.BeaconPivotExists()}, LowestInsertedBeaconHeaderNumber: {_blockTree.LowestInsertedBeaconHeader?.Number}, BeaconPivot: {_beaconPivot.PivotNumber}, BeaconPivotDestinationNumber: {_beaconPivot.PivotDestinationNumber}");
            return finished;
        }

        // At this point, beacon headers sync is finished and has found an ancestor that exists in the block tree
        // beacon sync moves forward from the ancestor and is finished when the block body gap is filled + processed
        // in the case of fast sync, this is the gap between the state sync head with beacon block head
        public bool IsBeaconSyncFinished(BlockHeader? blockHeader)
        {
            return !_beaconPivot.BeaconPivotExists()
                   || (blockHeader != null && _blockTree.WasProcessed(blockHeader.Number, blockHeader.Hash ?? blockHeader.CalculateHash()));
        }

        public bool DanglingChainMerged
        {
            get => _danglingChainMerged;
            set
            {
                _danglingChainMerged = value;
                if (value)
                {
                    _metadataDb.Set(MetadataDbKeys.DanglingChainMerged, Rlp.Encode(value).Bytes);
                }
            }
        }

        public bool FastSyncEnabled => _syncConfig.FastSync;
    }

    public interface IMergeSyncController
    {
        void SwitchToBeaconModeControl();

        void InitSyncing(BlockHeader? blockHeader);
        
        bool DanglingChainMerged { get; set; }
    }
}
