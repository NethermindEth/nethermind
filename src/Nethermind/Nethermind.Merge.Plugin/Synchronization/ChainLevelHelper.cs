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
/// Key assumptions:
/// - LowestInsertedBeaconHeader is monotonically decreasing — only lowered by backward beacon
///   header insertion, never raised by NewPayload or other operations.
/// - Both FastHeadersSyncFeed and BeaconHeadersSyncFeed insert headers contiguously via a
///   dependency queue. Once LowestInsertedBeaconHeader = K, all headers in [K, BeaconPivot]
///   are guaranteed present.
/// - On PoS chains, NeedToWaitForHeaders = false, so SyncMode.Full can run simultaneously
///   with SyncMode.FastHeaders.
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
    /// Determines whether the gap is transient (responsible feed still running) or
    /// genuine (feed completed the range but block is absent).
    ///
    /// Feed ranges and contiguity guarantees:
    ///   [0, N] (SyncPivot) — filled by FastHeadersSyncFeed, downloads backward in batches.
    ///     Headers are applied contiguously via a dependency queue, but gaps can exist while
    ///     the feed is still active (SyncMode.FastHeaders set). On PoS, SyncMode.Full can run
    ///     simultaneously with SyncMode.FastHeaders (NeedToWaitForHeaders = false).
    ///   [N+1, M] (ProcessDestination) — filled by BeaconHeadersSyncFeed, downloads backward.
    ///     LowestInsertedBeaconHeader is monotonically decreasing and marks the contiguous
    ///     frontier: all headers in [LowestInsertedBeaconHeader, M] are guaranteed present.
    ///
    /// Safety timer: if a "wait" decision persists longer than MissingBeaconHeaderSafetyTimeoutSec,
    /// a forced restart is triggered as defense-in-depth.
    ///
    /// See NethermindEth/nethermind#6304, #6611.
    /// </summary>
    private void OnMissingBeaconHeader(long blockNumber)
    {
        // Only act if ProcessDestination is set and the missing block is below it.
        // When ProcessDestination is null, there's no target to sync toward yet — not an error.
        if (_beaconPivot.ProcessDestination is null || _beaconPivot.ProcessDestination.Number <= blockNumber)
            return;

        if (_beaconPivot.ShouldForceStartNewSync)
            return;

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
    /// the gap is likely a transient batch hole — wait. Otherwise, the feed finished and this
    /// is a genuine gap — use the safety timer before forcing restart.
    /// </summary>
    private void HandleMissingInFastHeadersRange(long blockNumber)
    {
        SyncMode currentMode = _syncModeSelector.Current;
        bool fastHeadersActive = (currentMode & SyncMode.FastHeaders) != SyncMode.None;

        if (fastHeadersActive)
        {
            WaitWithSafetyTimer(blockNumber, "FastHeaders active, transient batch gap expected");
            return;
        }

        // FastHeaders not active — feed may have finished, leaving a genuine gap.
        // Use the safety timer to allow for brief SyncMode tick lag before forcing restart.
        if (HasSafetyTimerExpired())
        {
            ForceRestart(blockNumber, "block in FastHeaders range missing after feed inactive and safety timer expired");
            return;
        }

        WaitWithSafetyTimer(blockNumber, "FastHeaders not active, monitoring via safety timer");
    }

    /// <summary>
    /// Block is in the BeaconHeaders range (N+1, M]. LowestInsertedBeaconHeader marks the
    /// contiguous frontier — all headers at or above it are present.
    ///   blockNumber &lt; lowestBeacon → feed hasn't descended here yet → wait
    ///   blockNumber &gt;= lowestBeacon → feed already passed, block should exist → genuine gap
    /// </summary>
    private void HandleMissingInBeaconRange(long blockNumber)
    {
        long? lowestBeacon = _blockTree.LowestInsertedBeaconHeader?.Number;

        // Feed hasn't started (null) or hasn't descended to this block yet.
        if (lowestBeacon is null || blockNumber < lowestBeacon.Value)
        {
            WaitWithSafetyTimer(blockNumber,
                $"beacon feed hasn't reached block yet (lowest beacon header: {lowestBeacon?.ToString() ?? "none"})");
            return;
        }

        // blockNumber >= lowestBeacon: feed already passed this level. All headers
        // in [lowestBeacon, M] should be contiguously present. A missing block is a genuine gap.
        ForceRestart(blockNumber,
            $"genuine gap in beacon range (block {blockNumber} >= lowest beacon header {lowestBeacon.Value})");
    }

    private void WaitWithSafetyTimer(long blockNumber, string reason)
    {
        _waitStartedAt ??= _timestamper.UtcNow;

        if (HasSafetyTimerExpired())
        {
            ForceRestart(blockNumber, $"safety timer expired while waiting ({reason})");
            return;
        }

        if (_logger.IsDebug) _logger.Debug(
            $"Beacon header at height {blockNumber} missing. Waiting: {reason}.");
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
