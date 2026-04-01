// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Merge.Plugin.Synchronization;

public interface IChainLevelHelper
{
    BlockHeader[]? GetNextHeaders(int maxCount, long maxHeaderNumber, int skipLastBlockCount = 0);
}

/// <summary>
/// Navigates chain levels during forward beacon header processing.
/// Called by <see cref="PosForwardHeaderProvider"/> when SyncMode.Full or SyncMode.FastSync is active.
///
/// Key design notes:
/// - On PoS chains, NeedToWaitForHeaders = false, so SyncMode.Full can run simultaneously
///   with SyncMode.FastHeaders. Missing blocks in the FastHeaders range [0, SyncPivot] are
///   expected while the feed is active.
/// - LowestInsertedBeaconHeader exhibits a sawtooth pattern: EnsurePivot raises it to the
///   latest FCU head every slot (~12s), then BeaconHeadersSyncFeed drives it back down as it
///   downloads. This makes it unreliable as a contiguity marker. The beacon range guard uses
///   [PivotDestinationNumber, PivotNumber] instead.
/// - BeaconHeadersSyncFeed completes its range (typically &lt;64 blocks) in a single batch,
///   well before the next FCU. The sawtooth does not cause gaps in practice.
/// </summary>
public class ChainLevelHelper : IChainLevelHelper
{
    private readonly IBlockTree _blockTree;
    private readonly ISyncConfig _syncConfig;
    private readonly ILogger _logger;
    private readonly IBeaconPivot _beaconPivot;
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly ITimestamper _timestamper;
    private readonly TimeSpan _safetyTimerDuration;
    private DateTime? _waitStartedAt;

    public ChainLevelHelper(
        IBlockTree blockTree,
        IBeaconPivot beaconPivot,
        ISyncConfig syncConfig,
        ISyncModeSelector syncModeSelector,
        ITimestamper timestamper,
        ILogManager logManager)
    {
        _blockTree = blockTree;
        _beaconPivot = beaconPivot;
        _syncConfig = syncConfig;
        _syncModeSelector = syncModeSelector;
        _timestamper = timestamper;
        _safetyTimerDuration = TimeSpan.FromSeconds(syncConfig.MissingBeaconHeaderSafetyTimeoutSec);
        _logger = logManager.GetClassLogger();
    }

    /// <summary>
    /// Called when a block level is missing during forward header processing.
    /// Routes to the appropriate range handler based on the block's position relative
    /// to the global sync pivot. A safety timer forces restart if waiting too long.
    /// See NethermindEth/nethermind#6304, #6611.
    /// </summary>
    private void OnMissingBeaconHeader(long blockNumber)
    {
        if (_beaconPivot.ProcessDestination is null || _beaconPivot.ProcessDestination.Number <= blockNumber)
        {
            if (_logger.IsTrace) _logger.Trace(
                $"OnMissingBeaconHeader({blockNumber}) skipped: ProcessDestination={_beaconPivot.ProcessDestination?.Number.ToString() ?? "null"}");
            return;
        }

        if (_beaconPivot.ShouldForceStartNewSync)
        {
            if (_logger.IsTrace) _logger.Trace(
                $"OnMissingBeaconHeader({blockNumber}) skipped: ShouldForceStartNewSync already set");
            return;
        }

        long syncPivotNumber = _blockTree.SyncPivot.BlockNumber;

        if (blockNumber <= syncPivotNumber)
        {
            HandleMissingInFastHeadersRange(blockNumber);
        }
        else
        {
            HandleMissingInBeaconRange(blockNumber);
        }
    }

    /// <summary>
    /// Block is in the FastHeaders range [0, SyncPivot]. If FastHeaders is actively running,
    /// the gap is a transient batch hole — wait. Otherwise use the safety timer before restart.
    /// </summary>
    private void HandleMissingInFastHeadersRange(long blockNumber)
    {
        SyncMode currentMode = _syncModeSelector.Current;
        bool fastHeadersActive = (currentMode & SyncMode.FastHeaders) != SyncMode.None;

        if (fastHeadersActive)
        {
            WaitWithSafetyTimer(blockNumber, $"FastHeaders active (mode={currentMode}), transient batch gap");
            return;
        }

        if (HasSafetyTimerExpired())
        {
            ForceRestart(blockNumber, $"block in FastHeaders range missing after feed inactive (mode={currentMode}) and safety timer expired");
            return;
        }

        WaitWithSafetyTimer(blockNumber, $"FastHeaders not active (mode={currentMode}), monitoring via safety timer");
    }

    /// <summary>
    /// Block is above the global sync pivot — in the beacon range.
    /// Uses [PivotDestinationNumber, PivotNumber] to determine if the block is
    /// BeaconHeadersSyncFeed's responsibility. Does NOT use LowestInsertedBeaconHeader
    /// because EnsurePivot raises it to the latest FCU head every slot (sawtooth pattern),
    /// making it unreliable as a contiguity marker.
    /// </summary>
    private void HandleMissingInBeaconRange(long blockNumber)
    {
        long destinationNumber = _beaconPivot.PivotDestinationNumber;
        long pivotNumber = _beaconPivot.PivotNumber;

        if (blockNumber >= destinationNumber && blockNumber <= pivotNumber)
        {
            WaitWithSafetyTimer(blockNumber,
                $"block in beacon range [{destinationNumber}, {pivotNumber}]");
            return;
        }

        ForceRestart(blockNumber,
            $"block {blockNumber} outside beacon range [{destinationNumber}, {pivotNumber}]");
    }

    private void WaitWithSafetyTimer(long blockNumber, string reason)
    {
        bool timerJustStarted = _waitStartedAt is null;
        _waitStartedAt ??= _timestamper.UtcNow;

        if (HasSafetyTimerExpired())
        {
            ForceRestart(blockNumber, $"safety timer expired while waiting ({reason})");
            return;
        }

        if (timerJustStarted)
        {
            if (_logger.IsDebug) _logger.Debug(
                $"Beacon header at height {blockNumber} missing. Started safety timer. Waiting: {reason}. SyncPivot: {_blockTree.SyncPivot.BlockNumber}");
        }
        else
        {
            if (_logger.IsTrace) _logger.Trace(
                $"Beacon header at height {blockNumber} still missing. Waiting: {reason}.");
        }
    }

    private bool HasSafetyTimerExpired()
    {
        if (_waitStartedAt is null)
            return false;

        // Timer disabled (duration = 0) means "expire immediately" — no grace period.
        if (_safetyTimerDuration == TimeSpan.Zero)
            return true;

        TimeSpan elapsed = _timestamper.UtcNow - _waitStartedAt.Value;
        return elapsed >= _safetyTimerDuration;
    }

    private void ForceRestart(long blockNumber, string reason)
    {
        if (_logger.IsWarn) _logger.Warn(
            $"Unable to find beacon header at height {blockNumber}. Forcing new beacon sync: {reason}.");
        _beaconPivot.ShouldForceStartNewSync = true;
        _waitStartedAt = null;
    }

    public BlockHeader[]? GetNextHeaders(int maxCount, long maxHeaderNumber, int skipLastBlockCount = 0)
    {
        (long? startingPoint, Hash256? startingPointBlockHash) = GetStartingPoint();
        if (startingPoint is null)
        {
            if (_logger.IsTrace)
                _logger.Trace($"ChainLevelHelper.GetNextHeaders - starting point is null");
            return null;
        }

        if (_logger.IsTrace) _logger.Trace($"ChainLevelHelper.GetNextHeaders - starting point is {startingPoint}");

        int effectiveMax = maxCount + skipLastBlockCount;
        List<BlockHeader> headers = new(effectiveMax);
        int i = 0;

        while (i < effectiveMax)
        {
            ChainLevelInfo? level = _blockTree.FindLevel(startingPoint!.Value);

            BlockInfo? beaconMainChainBlock = startingPointBlockHash is not null ? level?.FindBlockInfo(startingPointBlockHash) : level?.BeaconMainChainBlock;
            startingPointBlockHash = null;

            if (level is null || beaconMainChainBlock is null)
            {
                OnMissingBeaconHeader(startingPoint.Value);
                if (_logger.IsTrace)
                    _logger.Trace($"ChainLevelHelper.GetNextHeaders - level {startingPoint} not found");
                break;
            }

            BlockHeader? newHeader =
                _blockTree.FindHeader(beaconMainChainBlock.BlockHash, BlockTreeLookupOptions.None);

            if (newHeader is null)
            {
                OnMissingBeaconHeader(startingPoint.Value);
                if (_logger.IsTrace) _logger.Trace($"ChainLevelHelper - header {startingPoint} not found");
                break;
            }

            if (_logger.IsTrace)
            {
                _logger.Trace($"ChainLevelHelper - MainChainBlock: {level.MainChainBlock} TD: {level.MainChainBlock?.TotalDifficulty}");
                foreach (BlockInfo bi in level.BlockInfos)
                {
                    _logger.Trace($"ChainLevelHelper {bi.BlockHash}, {bi.BlockNumber} {bi.TotalDifficulty} {bi.Metadata}");
                }
            }

            if (headers.Count > 0 && headers[^1].Hash != newHeader.ParentHash)
            {
                if (_logger.IsDebug) _logger.Debug($"ChainLevelHelper - header {startingPoint} is not canonical descendent of header before it. Hash: {newHeader.Hash}, Expected parent: {newHeader.ParentHash}, Actual parent: {headers[^1].Hash}. Could be a concurrent reorg.");

                break;
            }

            if (beaconMainChainBlock.IsBeaconInfo)
            {
                newHeader.TotalDifficulty = beaconMainChainBlock.TotalDifficulty == 0 ? null : beaconMainChainBlock.TotalDifficulty; // This is suppose to be removed, but I forgot to remove it before testing, so we only tested with this line in. Need to remove this back....
                if (beaconMainChainBlock.TotalDifficulty != 0)
                {
                    newHeader.TotalDifficulty = beaconMainChainBlock.TotalDifficulty;
                }
                else if (headers.Count > 0 && headers[^1].TotalDifficulty is not null)
                {
                    // The beacon header may not have the total difficulty available since it is downloaded
                    // backwards and final total difficulty may not be known early on. But this is still needed
                    // in order to know if a block is a terminal block.
                    // The first header should be a processed header, so the TD should be correct.
                    newHeader.TotalDifficulty = headers[^1].TotalDifficulty + newHeader.Difficulty;
                }
                else
                {
                    if (_logger.IsWarn)
                        _logger.Warn($"ChainLevelHelper - Unable to determine total difficulty. This is not expected. Header: {newHeader.ToString(BlockHeader.Format.FullHashAndNumber)}");
                    newHeader.TotalDifficulty = null;
                }
            }
            if (_logger.IsTrace)
                _logger.Trace(
                    $"ChainLevelHelper - A new block header {newHeader.ToString(BlockHeader.Format.FullHashAndNumber)}, header TD {newHeader.TotalDifficulty}");
            headers.Add(newHeader);
            ++i;
            if (i >= effectiveMax)
                break;

            ++startingPoint;
        }

        int toTake = headers.Count - skipLastBlockCount;
        if (toTake <= 0)
        {
            headers.Clear();
        }
        else
        {
            CollectionsMarshal.SetCount(headers, toTake);
        }

        return headers.ToArray();
    }

    /// <summary>
    /// Returns a number BEFORE the lowest beacon info where the forward beacon sync should start, or the latest
    /// block that was processed where we should continue processing.
    /// </summary>
    /// <returns></returns>
    private (long?, Hash256?) GetStartingPoint()
    {
        long startingPoint = Math.Min(_blockTree.BestKnownNumber + 1, _beaconPivot.ProcessDestination?.Number ?? long.MaxValue);
        bool shouldContinue;

        if (_logger.IsTrace) _logger.Trace($"ChainLevelHelper. starting point's starting point is {startingPoint}. Best known number: {_blockTree.BestKnownNumber}, Process destination: {_beaconPivot.ProcessDestination?.Number}");

        BlockInfo? beaconMainChainBlock = GetBeaconMainChainBlockInfo(startingPoint);
        if (beaconMainChainBlock is null)
        {
            OnMissingBeaconHeader(startingPoint);
            return default;
        }

        if (!beaconMainChainBlock.IsBeaconInfo)
        {
            return (startingPoint, beaconMainChainBlock.BlockHash);
        }
        BlockInfo? parentBlockInfo = null;
        Hash256 currentHash = beaconMainChainBlock.BlockHash;
        // in normal situation we will have one iteration of this loop, in some cases a few. Thanks to that we don't need to add extra pointer to manage forward syncing
        do
        {
            BlockHeader? header = _blockTree.FindHeader(currentHash!, BlockTreeLookupOptions.None);
            if (header is null)
            {
                if (_logger.IsTrace) _logger.Trace($"Header for number {startingPoint} was not found");
                return default;
            }

            parentBlockInfo = (_blockTree.GetInfo(header.Number - 1, header.ParentHash!)).Info;
            if (parentBlockInfo is null)
            {
                OnMissingBeaconHeader(header.Number);
                return default;
            }

            shouldContinue = parentBlockInfo.IsBeaconInfo;
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Searching for starting point on level {startingPoint}. Header: {header.ToString(BlockHeader.Format.FullHashAndNumber)}, BlockInfo: {parentBlockInfo.IsBeaconBody}, {parentBlockInfo.IsBeaconHeader}");

            // Note: the starting point, points to the non-beacon info block.
            // MergeBlockDownloader does not download the first header so this is deliberate
            --startingPoint;
            currentHash = header.ParentHash!;
            if (_syncConfig.FastSync && startingPoint < _beaconPivot.PivotDestinationNumber)
            {
                if (_logger.IsTrace) _logger.Trace($"Reached syncConfig pivot. Starting point: {startingPoint}");
                break;
            }
        } while (shouldContinue);

        return (startingPoint, parentBlockInfo.BlockHash);
    }

    private BlockInfo? GetBeaconMainChainBlockInfo(long startingPoint)
    {
        ChainLevelInfo? startingLevel = _blockTree.FindLevel(startingPoint);
        BlockInfo? beaconMainChainBlock = startingLevel?.BeaconMainChainBlock;
        if (beaconMainChainBlock is null)
        {
            if (_logger.IsTrace) _logger.Trace($"Beacon main chain block for number {startingPoint} was not found");
            return null;
        }

        return beaconMainChainBlock;
    }
}
