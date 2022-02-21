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

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin.Synchronization
{
    public class BeaconPivot : IBeaconPivot
    {
        private readonly ISyncConfig _syncConfig;
        private readonly IMergeConfig _mergeConfig;
        private readonly IDb _metadataDb;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private BlockHeader? _currentBeaconPivot;
        private BlockHeader? _pivotParent;
        private bool _pivotParentProcessed;

        public BeaconPivot(
            ISyncConfig syncConfig,
            IMergeConfig mergeConfig,
            IDb metadataDb,
            IBlockTree blockTree,
            ILogManager logManager)
        {
            _syncConfig = syncConfig;
            _mergeConfig = mergeConfig;
            _metadataDb = metadataDb;
            _blockTree = blockTree;
            _logger = logManager.GetClassLogger();
            PivotDestinationNumber = 0;
        }

        public long PivotNumber => _currentBeaconPivot?.Number ?? _syncConfig.PivotNumberParsed;

        public Keccak PivotHash => _currentBeaconPivot?.Hash ?? _syncConfig.PivotHashParsed;

        public UInt256? PivotTotalDifficulty => _currentBeaconPivot is null ?
            _syncConfig.PivotTotalDifficultyParsed : _mergeConfig.FinalTotalDifficultyParsed;
        
        public long PivotDestinationNumber { get; private set; }

        public void EnsurePivot(BlockHeader? blockHeader)
        {
            bool beaconPivotExists = BeaconPivotExists();
            // if (beaconPivotExists && blockHeader != null)
            // {
            //     _currentBeaconPivot = blockHeader;
            //     PivotDestinationNumber = CalculatePivotDestinationNumber(_currentBeaconPivot, blockHeader);
            // }
            
            if (!beaconPivotExists && blockHeader != null)
            {
                _currentBeaconPivot = blockHeader;
                // exclusive of fast sync pivot
                PivotDestinationNumber = _syncConfig.PivotNumberParsed == 0 ? 0 : _syncConfig.PivotNumberParsed + 1;
                _metadataDb.Set(MetadataDbKeys.BeaconSyncDestinationNumber, Rlp.Encode(PivotDestinationNumber).Bytes);
                _metadataDb.Set(MetadataDbKeys.BeaconSyncPivotNumber, Rlp.Encode(_currentBeaconPivot.Number).Bytes);
            }
        }

        public void ResetPivot()
        {
            _currentBeaconPivot = null;
            PivotDestinationNumber = 0;
        }

        public bool BeaconPivotExists() => _currentBeaconPivot != null;

        public bool  IsPivotParentProcessed()
        {
            EnsurePivotParentProcessed();
            return _pivotParentProcessed;
        }

        private void EnsurePivotParentProcessed()
        {
            if (_pivotParentProcessed || _currentBeaconPivot == null)
                return;

            if (_pivotParent == null)
                _pivotParent = _blockTree.FindParentHeader(_currentBeaconPivot!,
                    BlockTreeLookupOptions.TotalDifficultyNotNeeded);

            if (_pivotParent != null)
                _pivotParentProcessed = _blockTree.WasProcessed(_pivotParent.Number,
                    _pivotParent.Hash ?? _pivotParent.CalculateHash());
        }

        private long CalculatePivotDestinationNumber(BlockHeader oldPivotHeader, BlockHeader newPivotHeader)
        {
            if (newPivotHeader.Number > oldPivotHeader.Number)
            {
                return Math.Max(PivotDestinationNumber, oldPivotHeader.Number + 1);
            }

            return PivotDestinationNumber;
        }
    }

    public interface IBeaconPivot : IPivot
    {
        bool  IsPivotParentProcessed();

        void  EnsurePivot(BlockHeader? blockHeader);

        void ResetPivot();

        bool BeaconPivotExists();
    }
}
