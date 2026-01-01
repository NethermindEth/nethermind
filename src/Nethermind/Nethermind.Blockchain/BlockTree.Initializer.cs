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
                    right = index == 0 ? 0 : index - 1;
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
            ulong blockNumber = BestPersistedState ?? BestSuggestedHeader.Number;
            ChainLevelInfo chainLevelInfo = LoadLevel(blockNumber);
            BlockInfo? canonicalBlock = chainLevelInfo?.MainChainBlock;
            if (canonicalBlock is not null && canonicalBlock.WasProcessed)
            {
                SetHeadBlock(canonicalBlock.BlockHash!);
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

    private bool HeaderExists(ulong blockNumber, bool findBeacon = false)
    {
        ChainLevelInfo level = LoadLevel(blockNumber);
        if (level is null)
        {
            return false;
        }

        foreach (BlockInfo blockInfo in level.BlockInfos)
        {
            BlockHeader? header = FindHeader(blockInfo.BlockHash, BlockTreeLookupOptions.None);
            if (header is not null)
            {
                if (findBeacon && blockInfo.IsBeaconHeader)
                {
                    return true;
                }

                if (!findBeacon && !blockInfo.IsBeaconHeader)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool BodyExists(ulong blockNumber, bool findBeacon = false)
    {
        ChainLevelInfo level = LoadLevel(blockNumber);
        if (level is null)
        {
            return false;
        }

        foreach (BlockInfo blockInfo in level.BlockInfos)
        {
            Block? block = FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.None);
            if (block is not null)
            {
                if (findBeacon && blockInfo.IsBeaconBody)
                {
                    return true;
                }

                if (!findBeacon && !blockInfo.IsBeaconBody)
                {
                    return true;
                }
            }
        }

        return false;
    }
    private void LoadForkChoiceInfo()
    {
        Logger.Info("Loading fork choice info");
        FinalizedHash ??= _metadataDb.Get(MetadataDbKeys.FinalizedBlockHash)?.AsRlpStream().DecodeKeccak();
        SafeHash ??= _metadataDb.Get(MetadataDbKeys.SafeBlockHash)?.AsRlpStream().DecodeKeccak();
    }

    private void LoadLowestInsertedBeaconHeader()
    {
        if (_metadataDb.KeyExists(MetadataDbKeys.LowestInsertedBeaconHeaderHash))
        {
            Hash256? lowestBeaconHeaderHash = _metadataDb.Get(MetadataDbKeys.LowestInsertedBeaconHeaderHash)?
                .AsRlpStream().DecodeKeccak();
            _lowestInsertedBeaconHeader = FindHeader(lowestBeaconHeaderHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }
    }

    private void LoadLowestInsertedHeader()
    {
        if (_metadataDb.KeyExists(MetadataDbKeys.LowestInsertedFastHeaderHash))
        {
            Hash256? headerHash = _metadataDb.Get(MetadataDbKeys.LowestInsertedFastHeaderHash)?
                .AsRlpStream().DecodeKeccak();
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
        ulong left = (Head?.Number ?? 0) == 0
            ? Math.Max(SyncPivot.BlockNumber, LowestInsertedHeader?.Number ?? 0)
            : Head.Number;
        if (left > 0)
        {
            left--;
        }

        ulong right = left + (ulong)BestKnownSearchLimit;

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
        BestSuggestedHeader = FindHeader(bestSuggestedHeaderNumber, BlockTreeLookupOptions.None);
        BlockHeader? bestSuggestedBodyHeader = FindHeader(bestSuggestedBodyNumber, BlockTreeLookupOptions.None);
        BestSuggestedBody = bestSuggestedBodyHeader is null
            ? null
            : FindBlock(bestSuggestedBodyHeader.Hash, BlockTreeLookupOptions.None);
    }


    private void LoadBeaconBestKnown()
    {
        ulong left = Math.Max(Head?.Number ?? 0, LowestInsertedBeaconHeader?.Number ?? 0);
        if (left > 0)
        {
            left--;
        }

        ulong right = left + (ulong)BestKnownSearchLimit;
        ulong bestKnownNumberFound = BinarySearchBlockNumber(left, right, LevelExists, findBeacon: true) ?? 0;

        left = Math.Max(Math.Max(Head?.Number ?? 0, LowestInsertedBeaconHeader?.Number ?? 0), BestSuggestedHeader?.Number ?? 0);
        if (left > 0)
        {
            left--;
        }

        right = left + (ulong)BestKnownSearchLimit;
        ulong bestBeaconHeaderNumber = BinarySearchBlockNumber(left, right, HeaderExists, findBeacon: true) ?? 0;

        ulong? beaconPivotNumber = _metadataDb.Get(MetadataDbKeys.BeaconSyncPivotNumber)?.AsRlpValueContext().DecodeULong();
        left = Math.Max(Head?.Number ?? 0, beaconPivotNumber ?? 0);
        if (left > 0)
        {
            left--;
        }

        right = left + (ulong)BestKnownSearchLimit;
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
        BestSuggestedBeaconHeader = FindHeader(bestBeaconHeaderNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        BlockHeader? bestBeaconBodyHeader = FindHeader(bestBeaconBodyNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        BestSuggestedBeaconBody = bestBeaconBodyHeader is null
            ? null
            : FindBlock(bestBeaconBodyHeader.Hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
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
        byte[] persistedNumberData = _blockInfoDb.Get(StateHeadHashDbEntryAddress);
        BestPersistedState = persistedNumberData is null ? null : new RlpStream(persistedNumberData).DecodeULong();
        ulong? persistedNumber = BestPersistedState;
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
            _syncPivot = (checked((ulong)_syncConfig.PivotNumber), _syncConfig.PivotHash is null ? null : new Hash256(Bytes.FromHexString(_syncConfig.PivotHash)));
            return;
        }

        RlpStream pivotStream = new(pivotFromDb!);
        ulong updatedPivotBlockNumber = pivotStream.DecodeULong();
        Hash256 updatedPivotBlockHash = pivotStream.DecodeKeccak()!;

        if (updatedPivotBlockHash.IsZero)
        {
            _syncPivot = (checked((ulong)_syncConfig.PivotNumber), _syncConfig.PivotHash is null ? null : new Hash256(Bytes.FromHexString(_syncConfig.PivotHash)));
            return;
        }

        SyncPivot = (updatedPivotBlockNumber, updatedPivotBlockHash);
        _syncConfig.MaxAttemptsToUpdatePivot = 0; // Disable pivot updater

        if (Logger.IsInfo) Logger.Info($"Pivot block has been set based on data from db. Pivot block number: {updatedPivotBlockNumber}, hash: {updatedPivotBlockHash}");
    }
}
