// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain;

public partial class BlockTree
{
    private bool _tryToRecoverFromHeaderBelowBodyCorruption = false;

    public void RecalculateTreeLevels()
    {
        LoadLowestInsertedHeader();
        LoadLowestInsertedBeaconHeader();
        LoadBestKnown();
        LoadBeaconBestKnown();
        LoadForkChoiceInfo();
        FixLowestInsertedBeaconHeader();
    }

    private void FixLowestInsertedBeaconHeader()
    {
        BlockHeader? lowest = _lowestInsertedBeaconHeader;
        if (lowest is null)
        {
            return;
        }

        // An unclean shutdown between a beacon header write and its pointer update can leave
        // LowestInsertedBeaconHeader parked above headers the backfill had already inserted.
        // Walk down through the contiguous beacon segment and stop at the merge junction — the
        // first already-synced non-beacon block. Anchoring on IsKnownBlock keeps the walk bounded
        // to the beacon segment even when the suggested pointers are missing or stale.
        BlockHeader current = lowest;
        while (current.ParentHash is not null)
        {
            BlockHeader? parent = FindHeader(current.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (parent?.Hash is null || IsKnownBlock(parent.Number, parent.Hash))
            {
                break;
            }

            current = parent;
        }

        if (!ReferenceEquals(current, lowest))
        {
            if (Logger.IsInfo) Logger.Info($"Lowest inserted beacon header moved from {lowest.Number} down to {current.Number} through already inserted beacon headers");
            LowestInsertedBeaconHeader = current;
        }
    }

    public static ulong? BinarySearchBlockNumber(ulong left, ulong right, Func<ulong, bool, bool> isBlockFound,
        BinarySearchDirection direction = BinarySearchDirection.Up, bool findBeacon = false)
    {
        if (left > right)
        {
            return null;
        }

        ulong? result = null;
        while (left != right)
        {
            ulong index = direction == BinarySearchDirection.Up
                ? left + (right - left) / 2
                : right - (right - left) / 2;
            if (isBlockFound(index, findBeacon))
            {
                result = index;
                if (direction == BinarySearchDirection.Up)
                {
                    left = index + 1;
                }
                else
                {
                    if (index == 0) break; // avoid ulong wrap
                    right = index - 1;
                }
            }
            else
            {
                if (direction == BinarySearchDirection.Up)
                {
                    right = index;
                }
                else
                {
                    left = index;
                }
            }
        }

        if (isBlockFound(left, findBeacon))
        {
            result = direction == BinarySearchDirection.Up ? left : right;
        }

        return result;
    }

    private void AttemptToFixCorruptionByMovingHeadBackwards()
    {
        if (_tryToRecoverFromHeaderBelowBodyCorruption && BestSuggestedHeader is not null)
        {
            ulong? persistedNumber = _stateBoundary.BestPersistedState;
            ulong blockNumber = persistedNumber ?? BestSuggestedHeader.Number;
            ChainLevelInfo chainLevelInfo = LoadLevel(blockNumber);
            BlockInfo? canonicalBlock = chainLevelInfo?.MainChainBlock;
            if (canonicalBlock?.WasProcessed == true && FindBlock(canonicalBlock.BlockHash, BlockTreeLookupOptions.None) is not null)
            {
                SetHeadBlock(canonicalBlock.BlockHash!);
            }
            else if (canonicalBlock is { WasProcessed: false } && persistedNumber is null)
            {
                // The persisted ceiling is unavailable and the surviving suggested candidate was not processed;
                // load-time clamps are sufficient.
                if (Logger.IsInfo) Logger.Info("Skipping head rollback for 'header < body' recovery - persisted ceiling is unavailable and the surviving suggested candidate was not processed.");
            }
            else
            {
                Logger.Error("Failed attempt to fix 'header < body' corruption caused by an unexpected shutdown.");
            }
        }
    }

    private bool LevelExists(ulong blockNumber, bool findBeacon = false)
    {
        ChainLevelInfo? level = LoadLevel(blockNumber);
        if (findBeacon)
        {
            return level is not null && level.HasBeaconBlocks;
        }

        return level is not null && level.HasNonBeaconBlocks;
    }

    private bool HeaderExists(ulong blockNumber, bool findBeacon = false) =>
        FindHeaderAtLevel(blockNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing, findBeacon) is not null;

    private bool BodyExists(ulong blockNumber, bool findBeacon = false) =>
        FindBlockAtLevel(blockNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing, findBeacon) is not null;

    private void LoadForkChoiceInfo()
    {
        Logger.Info("Loading fork choice info");
        FinalizedHash ??= DecodeMetadataKeccak(MetadataDbKeys.FinalizedBlockHash);
        SafeHash ??= DecodeMetadataKeccak(MetadataDbKeys.SafeBlockHash);
        if (FinalizedHash is not null)
        {
            LastFinalizedBlockLevel = _headerStore.GetBlockNumber(FinalizedHash) ?? 0UL;
        }
    }

    private void LoadLowestInsertedBeaconHeader()
    {
        if (_metadataDb.KeyExists(MetadataDbKeys.LowestInsertedBeaconHeaderHash))
        {
            Hash256? lowestBeaconHeaderHash = DecodeMetadataKeccak(MetadataDbKeys.LowestInsertedBeaconHeaderHash);
            _lowestInsertedBeaconHeader = FindHeader(lowestBeaconHeaderHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }
    }

    private void LoadLowestInsertedHeader()
    {
        if (_metadataDb.KeyExists(MetadataDbKeys.LowestInsertedFastHeaderHash))
        {
            Hash256? headerHash = DecodeMetadataKeccak(MetadataDbKeys.LowestInsertedFastHeaderHash);
            _lowestInsertedHeader = FindHeader(headerHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }
        else
        {
            // Old style binary search.
            ulong left = 1UL;
            ulong right = SyncPivot.BlockNumber;

            LowestInsertedHeader = BinarySearchBlockHeader(left, right, LevelExists, BinarySearchDirection.Down);
        }

        if (Logger.IsDebug) Logger.Debug($"Lowest inserted header set to {LowestInsertedHeader?.Number.ToString() ?? "null"}");
    }

    private void LoadBestKnown()
    {
        ulong pivotOrLowest = Math.Max(SyncPivot.BlockNumber, LowestInsertedHeader?.Number ?? 0);
        ulong left = (Head?.Number ?? 0) == 0
            ? pivotOrLowest.SaturatingSub(1)
            : Head.Number;

        ulong right = left + BestKnownSearchLimit;

        ulong bestKnownNumberFound = BinarySearchBlockNumber(left, right, LevelExists) ?? 0;
        ulong bestSuggestedHeaderNumber = BinarySearchBlockNumber(left, right, HeaderExists) ?? 0;
        ulong bestSuggestedBodyNumber = BinarySearchBlockNumber(left, right, BodyExists) ?? 0;

        if (Logger.IsInfo)
            Logger.Info("Numbers resolved, " +
                         $"level = {bestKnownNumberFound}, " +
                         $"header = {bestSuggestedHeaderNumber}, " +
                         $"body = {bestSuggestedBodyNumber}");

        if (bestSuggestedHeaderNumber < bestSuggestedBodyNumber)
        {
            if (Logger.IsWarn)
                Logger.Warn(
                    $"Detected corrupted block tree data ({bestSuggestedHeaderNumber} < {bestSuggestedBodyNumber}) (possibly due to an unexpected shutdown). Attempting to fix by moving head backwards. This may fail and you may need to resync the node.");
            bestSuggestedBodyNumber = bestSuggestedHeaderNumber;
            _tryToRecoverFromHeaderBelowBodyCorruption = true;
        }

        BestKnownNumber = bestKnownNumberFound;
        // The canonical FindHeader(number)/FindBlock(number) miss post-merge levels with no
        // main-chain block — where suggested-but-unprocessed blocks live after a restart.
        BestSuggestedHeader = FindHeader(bestSuggestedHeaderNumber, BlockTreeLookupOptions.None)
            ?? FindHeaderAtLevel(bestSuggestedHeaderNumber, BlockTreeLookupOptions.DoNotCreateLevelIfMissing, findBeacon: false);
        BestSuggestedBody = FindBlock(bestSuggestedBodyNumber, BlockTreeLookupOptions.None)
            ?? FindBlockAtLevel(bestSuggestedBodyNumber, BlockTreeLookupOptions.DoNotCreateLevelIfMissing, findBeacon: false);
    }

    private BlockHeader? FindHeaderAtLevel(ulong blockNumber, BlockTreeLookupOptions options, bool findBeacon)
    {
        ChainLevelInfo? level = LoadLevel(blockNumber);
        if (level is null)
        {
            return null;
        }

        BlockHeader? found = null;
        foreach (BlockInfo blockInfo in level.BlockInfos)
        {
            if (blockInfo.IsBeaconHeader != findBeacon)
            {
                continue;
            }

            BlockHeader? header = FindHeader(blockInfo.BlockHash, options, blockNumber: blockNumber);
            if (header is null)
            {
                continue;
            }

            if (findBeacon)
            {
                // mirrors ChainLevelInfo.BeaconMainChainBlock
                if (blockInfo.IsBeaconMainChain)
                {
                    return header;
                }
                found ??= header;
            }
            else
            {
                // last entry: at equal height the latest suggestion wins
                found = header;
            }
        }

        return found;
    }

    private Block? FindBlockAtLevel(ulong blockNumber, BlockTreeLookupOptions options, bool findBeacon)
    {
        ChainLevelInfo? level = LoadLevel(blockNumber);
        if (level is null)
        {
            return null;
        }

        Block? found = null;
        foreach (BlockInfo blockInfo in level.BlockInfos)
        {
            if (findBeacon ? !blockInfo.IsBeaconBody : blockInfo.IsBeaconInfo)
            {
                continue;
            }

            Block? block = FindBlock(blockInfo.BlockHash, options, blockNumber: blockNumber);
            if (block is null)
            {
                continue;
            }

            if (findBeacon)
            {
                if (blockInfo.IsBeaconMainChain)
                {
                    return block;
                }
                found ??= block;
            }
            else
            {
                found = block;
            }
        }

        return found;
    }


    private void LoadBeaconBestKnown()
    {
        ulong left = Math.Max(Head?.Number ?? 0, LowestInsertedBeaconHeader?.Number ?? 0);
        left = left.SaturatingSub(1);
        ulong right = left + BestKnownSearchLimit;
        ulong bestKnownNumberFound = BinarySearchBlockNumber(left, right, LevelExists, findBeacon: true) ?? 0;

        ulong maxHeadOrLowest = Math.Max(Head?.Number ?? 0, LowestInsertedBeaconHeader?.Number ?? 0);
        left = Math.Max(maxHeadOrLowest, BestSuggestedHeader?.Number ?? 0);
        left = left.SaturatingSub(1);

        right = left + BestKnownSearchLimit;
        ulong bestBeaconHeaderNumber = BinarySearchBlockNumber(left, right, HeaderExists, findBeacon: true) ?? 0;

        ulong? beaconPivotNumber = DecodeMetadataULong(MetadataDbKeys.BeaconSyncPivotNumber);
        left = Math.Max(Head?.Number ?? 0, beaconPivotNumber ?? 0);
        left = left.SaturatingSub(1);
        right = left + BestKnownSearchLimit;
        ulong bestBeaconBodyNumber = BinarySearchBlockNumber(left, right, BodyExists, findBeacon: true) ?? 0;

        if (Logger.IsInfo)
            Logger.Info("Beacon Numbers resolved, " +
                         $"level = {bestKnownNumberFound}, " +
                         $"header = {bestBeaconHeaderNumber}, " +
                         $"body = {bestBeaconBodyNumber}");

        if (bestBeaconHeaderNumber < bestBeaconBodyNumber)
        {
            if (Logger.IsWarn)
                Logger.Warn(
                    $"Detected corrupted block tree data ({bestBeaconHeaderNumber} < {bestBeaconBodyNumber}) (possibly due to an unexpected shutdown). Attempting to fix by moving head backwards. This may fail and you may need to resync the node.");
            bestBeaconBodyNumber = bestBeaconHeaderNumber;
            _tryToRecoverFromHeaderBelowBodyCorruption = true;
        }

        BestKnownBeaconNumber = bestKnownNumberFound;
        // beacon entries first — the canonical lookup can resolve a non-beacon sibling
        BestSuggestedBeaconHeader = FindHeaderAtLevel(bestBeaconHeaderNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded, findBeacon: true)
            ?? FindHeader(bestBeaconHeaderNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        BestSuggestedBeaconBody = FindBlockAtLevel(bestBeaconBodyNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded, findBeacon: true)
            ?? FindBlock(bestBeaconBodyNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
    }

    private Hash256? DecodeMetadataKeccak(int key)
    {
        byte[]? rlp = _metadataDb.Get(key);
        return rlp is null ? null : new RlpReader(rlp).DecodeKeccakOrNull();
    }

    private ulong? DecodeMetadataULong(int key)
    {
        byte[]? rlp = _metadataDb.Get(key);
        return rlp is null ? null : new RlpReader(rlp).DecodeULong();
    }

    public enum BinarySearchDirection
    {
        Up,
        Down
    }

    private BlockHeader? BinarySearchBlockHeader(ulong left, ulong right, Func<ulong, bool, bool> isBlockFound,
        BinarySearchDirection direction = BinarySearchDirection.Up)
    {
        ulong? blockNumber = BinarySearchBlockNumber(left, right, isBlockFound, direction);
        if (blockNumber.HasValue)
        {
            ChainLevelInfo? level = LoadLevel(blockNumber.Value) ?? throw new InvalidDataException(
                    $"Missing chain level at number {blockNumber.Value}");
            BlockInfo blockInfo = level.BlockInfos[0];
            return FindHeader(blockInfo.BlockHash, BlockTreeLookupOptions.None);
        }

        return null;
    }

    private void LoadStartBlock()
    {
        Block? startBlock = null;
        ulong? persistedNumber = _stateBoundary.BestPersistedState;
        if (persistedNumber is not null)
        {
            startBlock = FindBlock(persistedNumber.Value, BlockTreeLookupOptions.None);
            if (Logger.IsInfo) Logger.Info(
                $"Start block loaded from reorg boundary - {persistedNumber} - {startBlock?.ToString(Block.Format.Short)}");
        }
        else
        {
            byte[] data = _blockInfoDb.Get(HeadAddressInDb);
            if (data is not null)
            {
                startBlock = FindBlock(new Hash256(data), BlockTreeLookupOptions.None);
                if (Logger.IsInfo) Logger.Info($"Start block loaded from HEAD - {startBlock?.ToString(Block.Format.Short)}");
            }
        }

        if (startBlock is not null)
        {
            if (startBlock.Hash is null)
            {
                throw new InvalidDataException("The start block hash is null.");
            }

            SetHeadBlock(startBlock.Hash);
        }

        // The removed BestPersistedState setter recomputed the sync pivot as a side effect during load; keep it.
        TryUpdateSyncPivot();
    }

    private void SetHeadBlock(Hash256 headHash)
    {
        Block? headBlock = FindBlock(headHash, BlockTreeLookupOptions.None) ?? throw new InvalidOperationException(
                "An attempt to set a head block that has not been stored in the DB.");
        ChainLevelInfo? level = LoadLevel(headBlock.Number);
        int? index = level?.FindIndex(headHash);
        if (!index.HasValue)
        {
            throw new InvalidDataException("Head block data missing from chain info");
        }

        headBlock.Header.TotalDifficulty = level.BlockInfos[index.Value].TotalDifficulty;
        Head = headBlock;
    }

    private void LoadSyncPivot()
    {
        byte[]? pivotFromDb = _metadataDb.Get(MetadataDbKeys.UpdatedPivotData);
        if (pivotFromDb is null)
        {
            _syncPivot = (_syncConfig.PivotNumber, _syncConfig.PivotHash is null ? null : new Hash256(Bytes.FromHexString(_syncConfig.PivotHash)));
            return;
        }

        RlpReader pivotReader = new(pivotFromDb!);
        ulong updatedPivotBlockNumber = pivotReader.DecodeULong();
        Hash256 updatedPivotBlockHash = pivotReader.DecodeKeccak();

        if (updatedPivotBlockHash.IsZero)
        {
            _syncPivot = (_syncConfig.PivotNumber, _syncConfig.PivotHash is null ? null : new Hash256(Bytes.FromHexString(_syncConfig.PivotHash)));
            return;
        }

        SyncPivot = (updatedPivotBlockNumber, updatedPivotBlockHash);
        _syncConfig.MaxAttemptsToUpdatePivot = 0; // Disable pivot updater

        if (Logger.IsInfo) Logger.Info($"Pivot block has been set based on data from db. Pivot block number: {updatedPivotBlockNumber}, hash: {updatedPivotBlockHash}");
    }
}
