// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain;

public partial class BlockTree
{
    private bool _tryToRecoverFromHeaderBelowBodyCorruption = false;

    public void RecalculateTreeLevels()
    {
        LoadLowestInsertedBodyNumber();
        LoadLowestInsertedHeader();
        LoadLowestInsertedBeaconHeader();
        LoadBestKnown();
        LoadBeaconBestKnown();
    }

    private void AttemptToFixCorruptionByMovingHeadBackwards()
    {
        if (_tryToRecoverFromHeaderBelowBodyCorruption && BestSuggestedHeader is not null)
        {
            ChainLevelInfo chainLevelInfo = LoadLevel(BestSuggestedHeader.Number);
            BlockInfo? canonicalBlock = chainLevelInfo?.MainChainBlock;
            if (canonicalBlock is not null && canonicalBlock.WasProcessed)
            {
                SetHeadBlock(canonicalBlock.BlockHash!);
            }
            else
            {
                _logger.Error("Failed attempt to fix 'header < body' corruption caused by an unexpected shutdown.");
            }
        }
    }

    private bool LevelExists(long blockNumber, bool findBeacon = false)
    {
        ChainLevelInfo? level = LoadLevel(blockNumber);
        if (findBeacon)
        {
            return level is not null && level.HasBeaconBlocks;
        }

        return level is not null && level.HasNonBeaconBlocks;
    }

    private bool HeaderExists(long blockNumber, bool findBeacon = false)
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

    private bool BodyExists(long blockNumber, bool findBeacon = false)
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

    private void LoadLowestInsertedBodyNumber()
    {
        LowestInsertedBodyNumber =
            _blockStore.GetMetadata(LowestInsertedBodyNumberDbEntryAddress)?
                .AsRlpValueContext().DecodeLong();
    }

    private void LoadLowestInsertedBeaconHeader()
    {
        if (_metadataDb.KeyExists(MetadataDbKeys.LowestInsertedBeaconHeaderHash))
        {
            Keccak? lowestBeaconHeaderHash = _metadataDb.Get(MetadataDbKeys.LowestInsertedBeaconHeaderHash)?
                .AsRlpStream().DecodeKeccak();
            _lowestInsertedBeaconHeader = FindHeader(lowestBeaconHeaderHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }
    }

    private void LoadLowestInsertedHeader()
    {
        long left = 1L;
        long right = _syncConfig.PivotNumberParsed;

        LowestInsertedHeader = BinarySearchBlockHeader(left, right, LevelExists, BinarySearchDirection.Down);
    }

    private void LoadBestKnown()
    {
        long left = (Head?.Number ?? 0) == 0
            ? Math.Max(_syncConfig.PivotNumberParsed, LowestInsertedHeader?.Number ?? 0) - 1
            : Head.Number;

        long right = Math.Max(0, left) + BestKnownSearchLimit;

        long bestKnownNumberFound =
            BinarySearchBlockNumber(1, left, LevelExists) ?? 0;
        long bestKnownNumberAlternative =
            BinarySearchBlockNumber(left, right, LevelExists) ?? 0;

        long bestSuggestedHeaderNumber =
            BinarySearchBlockNumber(1, left, HeaderExists) ?? 0;
        long bestSuggestedHeaderNumberAlternative
            = BinarySearchBlockNumber(left, right, HeaderExists) ?? 0;

        long bestSuggestedBodyNumber
            = BinarySearchBlockNumber(1, left, BodyExists) ?? 0;
        long bestSuggestedBodyNumberAlternative
            = BinarySearchBlockNumber(left, right, BodyExists) ?? 0;

        if (_logger.IsInfo)
            _logger.Info("Numbers resolved, " +
                         $"level = Max({bestKnownNumberFound}, {bestKnownNumberAlternative}), " +
                         $"header = Max({bestSuggestedHeaderNumber}, {bestSuggestedHeaderNumberAlternative}), " +
                         $"body = Max({bestSuggestedBodyNumber}, {bestSuggestedBodyNumberAlternative})");

        bestKnownNumberFound = Math.Max(bestKnownNumberFound, bestKnownNumberAlternative);
        bestSuggestedHeaderNumber = Math.Max(bestSuggestedHeaderNumber, bestSuggestedHeaderNumberAlternative);
        bestSuggestedBodyNumber = Math.Max(bestSuggestedBodyNumber, bestSuggestedBodyNumberAlternative);

        if (bestKnownNumberFound < 0 ||
            bestSuggestedHeaderNumber < 0 ||
            bestSuggestedBodyNumber < 0 ||
            bestSuggestedHeaderNumber < bestSuggestedBodyNumber)
        {
            if (_logger.IsWarn)
                _logger.Warn(
                    $"Detected corrupted block tree data ({bestSuggestedHeaderNumber} < {bestSuggestedBodyNumber}) (possibly due to an unexpected shutdown). Attempting to fix by moving head backwards. This may fail and you may need to resync the node.");
            if (bestSuggestedHeaderNumber < bestSuggestedBodyNumber)
            {
                bestSuggestedBodyNumber = bestSuggestedHeaderNumber;
                _tryToRecoverFromHeaderBelowBodyCorruption = true;
            }
            else
            {
                throw new InvalidDataException("Invalid initial block tree state loaded - " +
                                               $"best known: {bestKnownNumberFound}|" +
                                               $"best header: {bestSuggestedHeaderNumber}|" +
                                               $"best body: {bestSuggestedBodyNumber}|");
            }
        }

        BestKnownNumber = Math.Max(bestKnownNumberFound, bestKnownNumberAlternative);
        BestSuggestedHeader = FindHeader(bestSuggestedHeaderNumber, BlockTreeLookupOptions.None);
        BlockHeader? bestSuggestedBodyHeader = FindHeader(bestSuggestedBodyNumber, BlockTreeLookupOptions.None);
        BestSuggestedBody = bestSuggestedBodyHeader is null
            ? null
            : FindBlock(bestSuggestedBodyHeader.Hash, BlockTreeLookupOptions.None);
    }


    private void LoadBeaconBestKnown()
    {
        long left = Math.Max(Head?.Number ?? 0, LowestInsertedBeaconHeader?.Number ?? 0) - 1;
        long right = Math.Max(0, left) + BestKnownSearchLimit;
        long bestKnownNumberFound = BinarySearchBlockNumber(left, right, LevelExists, findBeacon: true) ?? 0;

        left = Math.Max(
            Math.Max(
                Head?.Number ?? 0,
                LowestInsertedBeaconHeader?.Number ?? 0),
            BestSuggestedHeader?.Number ?? 0
        ) - 1;

        right = Math.Max(0, left) + BestKnownSearchLimit;
        long bestBeaconHeaderNumber = BinarySearchBlockNumber(left, right, HeaderExists, findBeacon: true) ?? 0;

        long? beaconPivotNumber = _metadataDb.Get(MetadataDbKeys.BeaconSyncPivotNumber)?.AsRlpValueContext().DecodeLong();
        left = Math.Max(Head?.Number ?? 0, beaconPivotNumber ?? 0) - 1;
        right = Math.Max(0, left) + BestKnownSearchLimit;
        long bestBeaconBodyNumber = BinarySearchBlockNumber(left, right, BodyExists, findBeacon: true) ?? 0;

        if (_logger.IsInfo)
            _logger.Info("Beacon Numbers resolved, " +
                         $"level = {bestKnownNumberFound}, " +
                         $"header = {bestBeaconHeaderNumber}, " +
                         $"body = {bestBeaconBodyNumber}");

        if (bestKnownNumberFound < 0 ||
            bestBeaconHeaderNumber < 0 ||
            bestBeaconBodyNumber < 0 ||
            bestBeaconHeaderNumber < bestBeaconBodyNumber)
        {
            if (_logger.IsWarn)
                _logger.Warn(
                    $"Detected corrupted block tree data ({bestBeaconHeaderNumber} < {bestBeaconBodyNumber}) (possibly due to an unexpected shutdown). Attempting to fix by moving head backwards. This may fail and you may need to resync the node.");
            if (bestBeaconHeaderNumber < bestBeaconBodyNumber)
            {
                bestBeaconBodyNumber = bestBeaconHeaderNumber;
                _tryToRecoverFromHeaderBelowBodyCorruption = true;
            }
            else
            {
                throw new InvalidDataException("Invalid initial block tree state loaded - " +
                                               $"best known: {bestKnownNumberFound}|" +
                                               $"best header: {bestBeaconHeaderNumber}|" +
                                               $"best body: {bestBeaconBodyNumber}|");
            }
        }

        BestKnownBeaconNumber = bestKnownNumberFound;
        BestSuggestedBeaconHeader = FindHeader(bestBeaconHeaderNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        BlockHeader? bestBeaconBodyHeader = FindHeader(bestBeaconBodyNumber, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        BestSuggestedBeaconBody = bestBeaconBodyHeader is null
            ? null
            : FindBlock(bestBeaconBodyHeader.Hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
    }

    private enum BinarySearchDirection
    {
        Up,
        Down
    }

    private BlockHeader? BinarySearchBlockHeader(long left, long right, Func<long, bool, bool> isBlockFound,
        BinarySearchDirection direction = BinarySearchDirection.Up)
    {
        long? blockNumber = BinarySearchBlockNumber(left, right, isBlockFound, direction);
        if (blockNumber.HasValue)
        {
            ChainLevelInfo? level = LoadLevel(blockNumber.Value);
            if (level is null)
            {
                throw new InvalidDataException(
                    $"Missing chain level at number {blockNumber.Value}");
            }

            BlockInfo blockInfo = level.BlockInfos[0];
            return FindHeader(blockInfo.BlockHash, BlockTreeLookupOptions.None);
        }

        return null;
    }

    private static long? BinarySearchBlockNumber(long left, long right, Func<long, bool, bool> isBlockFound,
        BinarySearchDirection direction = BinarySearchDirection.Up, bool findBeacon = false)
    {
        if (left > right)
        {
            return null;
        }

        long? result = null;
        while (left != right)
        {
            long index = direction == BinarySearchDirection.Up
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

    private void LoadStartBlock()
    {
        Block? startBlock = null;
        byte[] persistedNumberData = _blockInfoDb.Get(StateHeadHashDbEntryAddress);
        BestPersistedState = persistedNumberData is null ? null : new RlpStream(persistedNumberData).DecodeLong();
        long? persistedNumber = BestPersistedState;
        if (persistedNumber is not null)
        {
            startBlock = FindBlock(persistedNumber.Value, BlockTreeLookupOptions.None);
            if (_logger.IsInfo) _logger.Info(
                $"Start block loaded from reorg boundary - {persistedNumber} - {startBlock?.ToString(Block.Format.Short)}");
        }
        else
        {
            byte[] data = _blockInfoDb.Get(HeadAddressInDb);
            if (data is not null)
            {
                startBlock = FindBlock(new Keccak(data), BlockTreeLookupOptions.None);
                if (_logger.IsInfo) _logger.Info($"Start block loaded from HEAD - {startBlock?.ToString(Block.Format.Short)}");
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

    private void SetHeadBlock(Keccak headHash)
    {
        Block? headBlock = FindBlock(headHash, BlockTreeLookupOptions.None);
        if (headBlock is null)
        {
            throw new InvalidOperationException(
                "An attempt to set a head block that has not been stored in the DB.");
        }

        ChainLevelInfo? level = LoadLevel(headBlock.Number);
        int? index = level?.FindIndex(headHash);
        if (!index.HasValue)
        {
            throw new InvalidDataException("Head block data missing from chain info");
        }

        headBlock.Header.TotalDifficulty = level.BlockInfos[index.Value].TotalDifficulty;
        Head = headBlock;
    }
}
