﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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
        private readonly IDb _metadataDb;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private BlockHeader? _currentBeaconPivot;

        private BlockHeader? CurrentBeaconPivot
        {
            get => _currentBeaconPivot;
            set
            {
                _currentBeaconPivot = value;
                if (value != null)
                {
                    _metadataDb.Set(MetadataDbKeys.BeaconSyncPivotHash,
                        Rlp.Encode(value.GetOrCalculateHash()).Bytes);
                    _metadataDb.Set(MetadataDbKeys.BeaconSyncPivotNumber,
                        Rlp.Encode(value.Number).Bytes);
                } else _metadataDb.Delete(MetadataDbKeys.BeaconSyncPivotHash);
            }
        }


        public BeaconPivot(
            ISyncConfig syncConfig,
            IDb metadataDb,
            IBlockTree blockTree,
            ILogManager logManager)
        {
            _syncConfig = syncConfig;
            _metadataDb = metadataDb;
            _blockTree = blockTree;
            _logger = logManager.GetClassLogger();
            LoadBeaconPivot();
        }

        public long PivotNumber => CurrentBeaconPivot?.Number ?? _syncConfig.PivotNumberParsed;

        public Keccak PivotHash => CurrentBeaconPivot?.Hash ?? _syncConfig.PivotHashParsed;

        public BlockHeader? ProcessDestination { get; set; }

        // We actually start beacon header sync from the pivot parent hash because hive test.... And because
        // we can I guess?
        public Keccak PivotParentHash => CurrentBeaconPivot?.ParentHash ?? _syncConfig.PivotHashParsed;

        public UInt256? PivotTotalDifficulty => CurrentBeaconPivot is null ?
            _syncConfig.PivotTotalDifficultyParsed : CurrentBeaconPivot.TotalDifficulty;

        public long PivotDestinationNumber => CurrentBeaconPivot is null
            ? 0
            : _syncConfig.PivotNumberParsed + 1;
        public void EnsurePivot(BlockHeader? blockHeader, bool updateOnlyIfNull = false)
        {
            bool beaconPivotExists = BeaconPivotExists();
            if (blockHeader != null)
            {
                if (beaconPivotExists && updateOnlyIfNull)
                {
                    return;
                }

                if (updateOnlyIfNull)
                {
                    if (_logger.IsInfo) _logger.Info($"BeaconPivot was null. Setting beacon pivot to {blockHeader}");
                }

                // ToDo Sarah in some cases this could be wrong
                if (beaconPivotExists && (PivotNumber > blockHeader.Number || blockHeader.Hash == PivotHash))
                {
                    return;
                }

                // BeaconHeaderSync actually starts from the parent of the pivot. So we need to to manually insert
                // the pivot itself here.
                _blockTree.Insert(blockHeader,
                    BlockTreeInsertHeaderOptions.BeaconHeaderInsert | BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
                CurrentBeaconPivot = blockHeader;
                _blockTree.LowestInsertedBeaconHeader = blockHeader;
                if (_logger.IsInfo) _logger.Info($"New beacon pivot: {blockHeader}");
            }
        }

        public void RemoveBeaconPivot()
        {
            if (_logger.IsInfo) _logger.Info($"Removing beacon pivot, previous pivot: {_currentBeaconPivot}");
            CurrentBeaconPivot = null;
        }

        public bool BeaconPivotExists() => CurrentBeaconPivot != null;

        private void LoadBeaconPivot()
        {
            if (_metadataDb.KeyExists(MetadataDbKeys.BeaconSyncPivotHash))
            {
                Keccak? pivotHash = _metadataDb.Get(MetadataDbKeys.BeaconSyncPivotHash)?
                    .AsRlpStream().DecodeKeccak();
                if (pivotHash != null)
                {
                    _currentBeaconPivot =
                        _blockTree.FindHeader(pivotHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                }
            }

            if (_logger.IsInfo) _logger.Info($"Loaded Beacon Pivot: {CurrentBeaconPivot}");
        }
    }

    public interface IBeaconPivot : IPivot
    {
        void EnsurePivot(BlockHeader? blockHeader, bool updateOnlyIfNull = false);

        void RemoveBeaconPivot();

        bool BeaconPivotExists();

        // Used as a hint for MergeBlockDownloader to check from what point should it start checking for beacon blocks
        // in case where the lowest beacon block is lower than the best known number. This header moves forward
        // as MergeBlockDownloader process higher block, making it somewhat like a lowest processed beacon block.
        // TODO: Check if we can just re-use pivot and move pivot forward
        BlockHeader? ProcessDestination { get; set; }
    }
}
