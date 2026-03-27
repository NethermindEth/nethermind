// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Blockchain;

public partial class BlockTree
{
    private const string PersistedBoundaryRepairLogMarker = "PERSISTED-BOUNDARY-REPAIR";
    private bool _tryToRecoverFromHeaderBelowBodyCorruption = false;

    public void RecalculateTreeLevels()
    {
        LoadLowestInsertedHeader();
        LoadLowestInsertedBeaconHeader();
        LoadBestKnown();
        LoadBeaconBestKnown();
        LoadForkChoiceInfo();
    }

    public static long? BinarySearchBlockNumber(long left, long right, Func<long, bool, bool> isBlockFound,
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

    private void AttemptToFixCorruptionByMovingHeadBackwards()
    {
        if (_tryToRecoverFromHeaderBelowBodyCorruption && BestSuggestedHeader is not null)
        {
            long blockNumber = BestPersistedState ?? BestSuggestedHeader.Number;
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
            BlockHeader? header = FindHeader(blockInfo.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
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
            Block? block = FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
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
        FinalizedHash ??= _metadataDb.Get(MetadataDbKeys.FinalizedBlockHash)?.AsRlpValueContext().DecodeKeccak();
        SafeHash ??= _metadataDb.Get(MetadataDbKeys.SafeBlockHash)?.AsRlpValueContext().DecodeKeccak();
    }

    private void LoadLowestInsertedBeaconHeader()
    {
        if (_metadataDb.KeyExists(MetadataDbKeys.LowestInsertedBeaconHeaderHash))
        {
            Hash256? lowestBeaconHeaderHash = _metadataDb.Get(MetadataDbKeys.LowestInsertedBeaconHeaderHash)?
                .AsRlpValueContext().DecodeKeccak();
            _lowestInsertedBeaconHeader = FindHeader(lowestBeaconHeaderHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }
    }

    private void LoadLowestInsertedHeader()
    {
        if (_metadataDb.KeyExists(MetadataDbKeys.LowestInsertedFastHeaderHash))
        {
            Hash256? headerHash = _metadataDb.Get(MetadataDbKeys.LowestInsertedFastHeaderHash)?
                .AsRlpValueContext().DecodeKeccak();
            _lowestInsertedHeader = FindHeader(headerHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }
        else
        {
            // Old style binary search.
            long left = 1L;
            long right = SyncPivot.BlockNumber;

            LowestInsertedHeader = BinarySearchBlockHeader(left, right, LevelExists, BinarySearchDirection.Down);
        }

        if (Logger.IsDebug) Logger.Debug($"Lowest inserted header set to {LowestInsertedHeader?.Number.ToString() ?? "null"}");
    }

    private void LoadBestKnown()
    {
        long left = (Head?.Number ?? 0) == 0
            ? Math.Max(SyncPivot.BlockNumber, LowestInsertedHeader?.Number ?? 0) - 1
            : Head.Number;

        long right = Math.Max(0, left) + BestKnownSearchLimit;

        long bestKnownNumberFound = BinarySearchBlockNumber(left, right, LevelExists) ?? 0;
        long bestSuggestedHeaderNumber = BinarySearchBlockNumber(left, right, HeaderExists) ?? 0;
        long bestSuggestedBodyNumber = BinarySearchBlockNumber(left, right, BodyExists) ?? 0;

        if (Logger.IsInfo)
            Logger.Info("Numbers resolved, " +
                         $"level = {bestKnownNumberFound}, " +
                         $"header = {bestSuggestedHeaderNumber}, " +
                         $"body = {bestSuggestedBodyNumber}");

        if (bestKnownNumberFound < 0 ||
            bestSuggestedHeaderNumber < 0 ||
            bestSuggestedBodyNumber < 0 ||
            bestSuggestedHeaderNumber < bestSuggestedBodyNumber)
        {
            if (Logger.IsWarn)
                Logger.Warn(
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

        BestKnownNumber = bestKnownNumberFound;
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

        if (Logger.IsInfo)
            Logger.Info("Beacon Numbers resolved, " +
                         $"level = {bestKnownNumberFound}, " +
                         $"header = {bestBeaconHeaderNumber}, " +
                         $"body = {bestBeaconBodyNumber}");

        if (bestKnownNumberFound < 0 ||
            bestBeaconHeaderNumber < 0 ||
            bestBeaconBodyNumber < 0 ||
            bestBeaconHeaderNumber < bestBeaconBodyNumber)
        {
            if (Logger.IsWarn)
                Logger.Warn(
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

    public enum BinarySearchDirection
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
        string reconciliationOutcome = "startup boundary unavailable";
        byte[] persistedNumberData = _blockInfoDb.Get(StateHeadHashDbEntryAddress);
        byte[]? persistedHashData = _blockInfoDb.Get(StateHeadBlockHashDbEntryAddress);
        BestPersistedState = persistedNumberData is null ? null : new Rlp.ValueDecoderContext(persistedNumberData).DecodeLong();
        long? persistedNumber = BestPersistedState;
        Hash256? persistedHash = persistedHashData is null ? null : new Hash256(persistedHashData);
        if (persistedNumber is not null)
        {
            startBlock = TryLoadStartBlockFromExactBoundary(persistedNumber.Value, persistedHash)
                ?? FindBlock(persistedNumber.Value, BlockTreeLookupOptions.None);
            if (Logger.IsInfo) Logger.Info(
                $"Start block loaded from reorg boundary - {persistedNumber} - {startBlock?.ToString(Block.Format.Short)}");
            reconciliationOutcome = persistedHash is null
                ? "loaded from number-only persisted boundary metadata"
                : "loaded from exact persisted-boundary hash metadata";
        }
        else
        {
            byte[] data = _blockInfoDb.Get(HeadAddressInDb);
            if (data is not null)
            {
                startBlock = FindBlock(new Hash256(data), BlockTreeLookupOptions.None);
                if (Logger.IsInfo) Logger.Info($"Start block loaded from HEAD - {startBlock?.ToString(Block.Format.Short)}");
                reconciliationOutcome = "loaded from legacy HEAD metadata";
            }
        }

        PersistedStateInfo? persistedStateInfoValue = null;
        if (_persistedStateInfoProvider?.TryGetPersistedStateInfo(out PersistedStateInfo persistedStateInfo) == true)
        {
            persistedStateInfoValue = persistedStateInfo;
        }

        if (persistedStateInfoValue is PersistedStateInfo exactPersistedState &&
            startBlock is not null &&
            !MatchesPersistedState(startBlock, exactPersistedState))
        {
            startBlock = RepairPersistedBoundary(startBlock, persistedNumber, persistedHash, exactPersistedState, out reconciliationOutcome);
        }
        else if (persistedStateInfoValue is PersistedStateInfo)
        {
            reconciliationOutcome = "validated against exact persisted state";
        }
        else if (startBlock is not null)
        {
            reconciliationOutcome = "persisted state info unavailable";
        }

        if (Logger.IsInfo)
        {
            BlockInfo? canonicalInfo = startBlock is null ? null : FindCanonicalBlockInfo(startBlock.Number);
            bool? isCanonical = startBlock is null || startBlock.Hash is null
                ? null
                : canonicalInfo?.BlockHash == startBlock.Hash;
            bool? hasRecoverableState = startBlock is null
                ? null
                : _persistedStateInfoProvider?.HasRecoverableStateForBlock(startBlock.Header);
            string persistedStateDescription = _persistedStateInfoProvider?.TryGetPersistedStateInfo(out PersistedStateInfo stateInfo) == true
                ? $"{stateInfo.BlockNumber} / {stateInfo.StateRoot}"
                : "<unavailable>";

            Logger.Info(
                "Startup state diagnostics: " +
                $"stored persisted boundary number={persistedNumber?.ToString() ?? "<none>"}, " +
                $"stored persisted boundary hash={persistedHash?.ToString() ?? "<none>"}, " +
                $"flat persisted state={persistedStateDescription}, " +
                $"restored block={(startBlock?.ToString(Block.Format.Short) ?? "<none>")}, " +
                $"restored canonical={isCanonical?.ToString() ?? "<n/a>"}, " +
                $"restored recoverable={hasRecoverableState?.ToString() ?? "<n/a>"}.");
        }

        if (startBlock is not null)
        {
            if (startBlock.Hash is null)
            {
                throw new InvalidDataException("The start block hash is null.");
            }

            SetHeadBlock(startBlock.Hash);
            if (Logger.IsInfo)
            {
                Logger.Info($"Startup head set to {Head?.ToString(Block.Format.Short)}, state root: {Head?.StateRoot}.");
            }
        }

        LastStartupBoundaryDiagnostics = new StartupBoundaryDiagnostics(
            persistedNumber,
            persistedHash,
            persistedStateInfoValue,
            reconciliationOutcome);
    }

    private Block? TryLoadStartBlockFromExactBoundary(long persistedNumber, Hash256? persistedHash)
    {
        if (persistedHash is null)
        {
            return null;
        }

        Block? exactBlock = FindBlock(persistedHash, BlockTreeLookupOptions.None);
        if (exactBlock is null)
        {
            if (Logger.IsWarn)
            {
                Logger.Warn($"Stored persisted boundary hash {persistedHash} could not be resolved at startup.");
            }

            return null;
        }

        if (exactBlock.Number != persistedNumber)
        {
            if (Logger.IsWarn)
            {
                Logger.Warn(
                    $"Stored persisted boundary hash {persistedHash} resolved to block number {exactBlock.Number}, expected {persistedNumber}. Falling back to repair.");
            }

            return null;
        }

        return exactBlock;
    }

    private static bool MatchesPersistedState(Block block, PersistedStateInfo persistedStateInfo) =>
        block.Number == persistedStateInfo.BlockNumber &&
        block.StateRoot == persistedStateInfo.StateRoot;

    private Block RepairPersistedBoundary(Block restoredBlock, long? persistedNumber, Hash256? persistedHash, PersistedStateInfo persistedStateInfo, out string reconciliationOutcome)
    {
        Block? repairedBlock = FindBlockByStateRoot(persistedStateInfo.BlockNumber, persistedStateInfo.StateRoot) ??
            FindRecoverableCanonicalBoundary(Math.Min(restoredBlock.Number, persistedStateInfo.BlockNumber));

        if (repairedBlock is null)
        {
            reconciliationOutcome = "repair failed";
            throw new InvalidDataException(
                $"{PersistedBoundaryRepairLogMarker}: failed to repair persisted boundary. " +
                $"stored number={persistedNumber?.ToString() ?? "<none>"}, stored hash={persistedHash?.ToString() ?? "<none>"}, " +
                $"restored block={restoredBlock.ToString(Block.Format.Short)}, " +
                $"flat persisted state={persistedStateInfo.BlockNumber}/{persistedStateInfo.StateRoot}.");
        }

        if (Logger.IsWarn)
        {
            Logger.Warn(
                $"{PersistedBoundaryRepairLogMarker}: persisted boundary repaired from {restoredBlock.ToString(Block.Format.Short)} " +
                $"to {repairedBlock.ToString(Block.Format.Short)}.");
        }

        reconciliationOutcome = $"repaired to {repairedBlock.ToString(Block.Format.Short)}";
        BestPersistedState = repairedBlock.Number;
        return repairedBlock;
    }

    private Block? FindBlockByStateRoot(long blockNumber, ValueHash256 stateRoot)
    {
        ChainLevelInfo? level = LoadLevel(blockNumber);
        if (level is null)
        {
            return null;
        }

        foreach (BlockInfo blockInfo in level.BlockInfos)
        {
            Block? block = FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.None, blockNumber);
            if (block?.StateRoot == stateRoot)
            {
                return block;
            }
        }

        return null;
    }

    private Block? FindRecoverableCanonicalBoundary(long startNumber)
    {
        for (long number = startNumber; number >= _genesisBlockNumber; number--)
        {
            BlockInfo? canonicalInfo = FindCanonicalBlockInfo(number);
            if (canonicalInfo?.BlockHash is not Hash256 blockHash)
            {
                continue;
            }

            Block? canonicalBlock = FindBlock(blockHash, BlockTreeLookupOptions.None, number);
            if (canonicalBlock is not null && _persistedStateInfoProvider?.HasRecoverableStateForBlock(canonicalBlock.Header) == true)
            {
                return canonicalBlock;
            }
        }

        return null;
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

        Rlp.ValueDecoderContext pivotStream = new(pivotFromDb!);
        long updatedPivotBlockNumber = pivotStream.DecodeLong();
        Hash256 updatedPivotBlockHash = pivotStream.DecodeKeccak()!;

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
