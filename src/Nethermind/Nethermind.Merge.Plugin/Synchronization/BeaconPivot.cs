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

        public BlockHeader? CurrentBeaconPivot
        {
            get => _currentBeaconPivot;
            private set
            {
                if (_currentBeaconPivot != value)
                {
                    _currentBeaconPivot = value;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private BlockHeader? _pivotParent;
        private bool _pivotParentProcessed;
        private BlockHeader? _currentBeaconPivot;

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
            // _currentBeaconPivot = _blockTree.LowestInsertedBeaconHeader; // ToDo Sarah: I think it is incorrect, but we should discuss it
        }

        public long PivotNumber => CurrentBeaconPivot?.Number ?? _syncConfig.PivotNumberParsed;

        public Keccak PivotHash => CurrentBeaconPivot?.Hash ?? _syncConfig.PivotHashParsed;

        public UInt256? PivotTotalDifficulty => CurrentBeaconPivot is null ?
            _syncConfig.PivotTotalDifficultyParsed : CurrentBeaconPivot.TotalDifficulty;

        public long PivotDestinationNumber => CurrentBeaconPivot is null
            ? 0
            // :  Math.Max(_syncConfig.PivotNumberParsed, _blockTree.BestSuggestedHeader?.Number ?? 0) + 1; // ToDo Sarah the current code is not ready to go with BestSuggestedHeader. I see that beacon finished is trying to reach _syncConfig and we're stuck because of that
            : _syncConfig.PivotNumberParsed + 1;

        public event EventHandler? Changed;

        public void EnsurePivot(BlockHeader? blockHeader)
        {
            bool beaconPivotExists = BeaconPivotExists();
            if (blockHeader != null)
            {
                // ToDo Sarah in some cases this could be wrong
                if (beaconPivotExists && (PivotNumber > blockHeader.Number || blockHeader.Hash == PivotHash))
                {
                    return;
                }
                
                CurrentBeaconPivot = blockHeader;
                _blockTree.LowestInsertedBeaconHeader = blockHeader;
                if (_logger.IsInfo) _logger.Info($"New beacon pivot: {blockHeader}");
            }
        }

        public void RemoveBeaconPivot()
        {
            if (_logger.IsInfo) _logger.Info($"Removing beacon pivot, previous pivot: {CurrentBeaconPivot}");
            CurrentBeaconPivot = null;
            // ToDo clear DB
        }

        public bool BeaconPivotExists() => CurrentBeaconPivot != null;
    }

    public interface IBeaconPivot : IPivot
    {
        void  EnsurePivot(BlockHeader? blockHeader);

        void RemoveBeaconPivot();

        bool BeaconPivotExists();
    }
}
