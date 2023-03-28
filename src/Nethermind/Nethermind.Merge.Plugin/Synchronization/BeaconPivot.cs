// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
                if (value is not null)
                {
                    _metadataDb.Set(MetadataDbKeys.BeaconSyncPivotHash,
                        Rlp.Encode(value.GetOrCalculateHash()).Bytes);
                    _metadataDb.Set(MetadataDbKeys.BeaconSyncPivotNumber,
                        Rlp.Encode(value.Number).Bytes);
                }
                else _metadataDb.Delete(MetadataDbKeys.BeaconSyncPivotHash);
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

        public Keccak? PivotHash => CurrentBeaconPivot?.Hash ?? _syncConfig.PivotHashParsed;

        public BlockHeader? ProcessDestination { get; set; }
        public bool ShouldForceStartNewSync { get; set; } = false;

        // We actually start beacon header sync from the pivot parent hash because hive test.... And because
        // we can I guess?
        public Keccak? PivotParentHash => CurrentBeaconPivot?.ParentHash ?? _syncConfig.PivotHashParsed;

        public UInt256? PivotTotalDifficulty => CurrentBeaconPivot is null ?
            _syncConfig.PivotTotalDifficultyParsed : CurrentBeaconPivot.TotalDifficulty;

        // The stopping point (inclusive) for the reverse beacon header sync.
        public long PivotDestinationNumber
        {
            get
            {
                if (CurrentBeaconPivot is null)
                {
                    // Need to rethink if this is expected. Maybe it need to forward sync without a pivot.
                    return 0;
                }

                // If head is not null, that means we processed some block before.
                // It is possible that the head is lower than the sync pivot (restart with a new pivot) so we need to account for that.
                if (_blockTree.Head is not null && _blockTree.Head?.Number != 0)
                {
                    // However, the head may not be canon, so the destination need to be before that.
                    long safeNumber = _blockTree.Head!.Number - Reorganization.MaxDepth + 1;
                    return Math.Max(1, safeNumber);
                }

                return _syncConfig.PivotNumberParsed + 1;
            }
        }

        public void EnsurePivot(BlockHeader? blockHeader, bool updateOnlyIfNull = false)
        {
            bool beaconPivotExists = BeaconPivotExists();
            if (blockHeader is not null)
            {
                if (beaconPivotExists && updateOnlyIfNull)
                {
                    return;
                }

                if (updateOnlyIfNull)
                {
                    if (_logger.IsInfo) _logger.Info($"BeaconPivot was null. Setting beacon pivot to {blockHeader.ToString(BlockHeader.Format.FullHashAndNumber)}");
                }

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
                ShouldForceStartNewSync = false;
                if (_logger.IsInfo) _logger.Info($"New beacon pivot: {blockHeader.ToString(BlockHeader.Format.FullHashAndNumber)}");
            }
        }

        public void RemoveBeaconPivot()
        {
            if (_logger.IsInfo) _logger.Info($"Removing beacon pivot, previous pivot: {_currentBeaconPivot}");
            CurrentBeaconPivot = null;
        }

        public bool BeaconPivotExists() => CurrentBeaconPivot is not null;

        private void LoadBeaconPivot()
        {
            if (_metadataDb.KeyExists(MetadataDbKeys.BeaconSyncPivotHash))
            {
                Keccak? pivotHash = _metadataDb.Get(MetadataDbKeys.BeaconSyncPivotHash)?
                    .AsRlpStream().DecodeKeccak();
                if (pivotHash is not null)
                {
                    _currentBeaconPivot =
                        _blockTree.FindHeader(pivotHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                }
            }

            if (_logger.IsInfo) _logger.Info($"Loaded Beacon Pivot: {CurrentBeaconPivot?.ToString(BlockHeader.Format.FullHashAndNumber)}");
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
        bool ShouldForceStartNewSync { get; set; }
    }
}
