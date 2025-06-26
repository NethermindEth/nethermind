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

namespace Nethermind.Merge.Plugin.Synchronization;

public interface IChainLevelHelper
{
    BlockHeader[]? GetNextHeaders(int maxCount, long maxHeaderNumber, int skipLastBlockCount = 0);
}

public class ChainLevelHelper : IChainLevelHelper
{
    private readonly IBlockTree _blockTree;
    private readonly ISyncConfig _syncConfig;
    private readonly ILogger _logger;
    private readonly IBeaconPivot _beaconPivot;

    public ChainLevelHelper(
        IBlockTree blockTree,
        IBeaconPivot beaconPivot,
        ISyncConfig syncConfig,
        ILogManager logManager)
    {
        _blockTree = blockTree;
        _beaconPivot = beaconPivot;
        _syncConfig = syncConfig;
        _logger = logManager.GetClassLogger();
    }

    private void OnMissingBeaconHeader(long blockNumber)
    {
        if (_beaconPivot.ProcessDestination?.Number > blockNumber)
        {
            // For some reason, this block number is missing when it should not.
            // anyway, lets just restart the whole sync.
            if (_beaconPivot.ShouldForceStartNewSync) return;

            if (_logger.IsWarn) _logger.Warn($"Unable to find beacon header at height {blockNumber}. This is unexpected, forcing a new beacon sync.");
            _beaconPivot.ShouldForceStartNewSync = true;
        }
    }

    public BlockHeader[]? GetNextHeaders(int maxCount, long maxHeaderNumber, int skipLastBlockCount = 0)
    {
        long? startingPoint = GetStartingPoint();
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
            BlockInfo? beaconMainChainBlock = level?.BeaconMainChainBlock;
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
    private long? GetStartingPoint()
    {
        long startingPoint = Math.Min(_blockTree.BestKnownNumber + 1, _beaconPivot.ProcessDestination?.Number ?? long.MaxValue);
        bool shouldContinue;

        if (_logger.IsTrace) _logger.Trace($"ChainLevelHelper. starting point's starting point is {startingPoint}. Best known number: {_blockTree.BestKnownNumber}, Process destination: {_beaconPivot.ProcessDestination?.Number}");

        BlockInfo? beaconMainChainBlock = GetBeaconMainChainBlockInfo(startingPoint);
        if (beaconMainChainBlock is null)
        {
            OnMissingBeaconHeader(startingPoint);
            return null;
        }

        if (!beaconMainChainBlock.IsBeaconInfo)
        {
            return startingPoint;
        }

        Hash256 currentHash = beaconMainChainBlock.BlockHash;
        // in normal situation we will have one iteration of this loop, in some cases a few. Thanks to that we don't need to add extra pointer to manage forward syncing
        do
        {
            BlockHeader? header = _blockTree.FindHeader(currentHash!, BlockTreeLookupOptions.None);
            if (header is null)
            {
                if (_logger.IsTrace) _logger.Trace($"Header for number {startingPoint} was not found");
                return null;
            }

            BlockInfo? parentBlockInfo = (_blockTree.GetInfo(header.Number - 1, header.ParentHash!)).Info;
            if (parentBlockInfo is null)
            {
                OnMissingBeaconHeader(header.Number);
                return null;
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

        return startingPoint;
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
