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

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Synchronization.Blocks;

namespace Nethermind.Merge.Plugin.Synchronization;

public interface IChainLevelHelper
{
    BlockHeader[]? GetNextHeaders(int maxCount);

    bool TrySetNextBlocks(int maxCount, BlockDownloadContext context);
}

public class ChainLevelHelper : IChainLevelHelper
{
    private readonly IBlockTree _blockTree;
    private readonly ISyncConfig _syncConfig;
    private readonly ILogger _logger;

    public ChainLevelHelper(
        IBlockTree blockTree,
        ISyncConfig syncConfig,
        ILogManager logManager)
    {
        _blockTree = blockTree;
        _syncConfig = syncConfig;
        _logger = logManager.GetClassLogger();
    }

    public BlockHeader[]? GetNextHeaders(int maxCount)
    {
        long? startingPoint = GetStartingPoint();
        if (startingPoint == null)
            return null;

        List<BlockHeader> headers = new(maxCount);
        int i = 0;

        while (i < maxCount)
        {
            ChainLevelInfo? level = _blockTree.FindLevel(startingPoint!.Value);
            BlockInfo? beaconMainChainBlock = level?.BeaconMainChainBlock;
            if (level == null || beaconMainChainBlock == null)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"ChainLevelHelper.GetNextHeaders - level {startingPoint} not found");
                break;
            }

            BlockHeader? newHeader =
                _blockTree.FindHeader(beaconMainChainBlock.BlockHash, BlockTreeLookupOptions.None);

            if (newHeader == null)
            {
                if (_logger.IsTrace) _logger.Trace($"ChainLevelHelper - header {startingPoint} not found");
                continue;
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
                newHeader.TotalDifficulty = beaconMainChainBlock.TotalDifficulty == 0 ? null : beaconMainChainBlock.TotalDifficulty;
            if (_logger.IsTrace)
                _logger.Trace(
                    $"ChainLevelHelper - A new block header {newHeader.ToString(BlockHeader.Format.FullHashAndNumber)}, header TD {newHeader.TotalDifficulty}");
            headers.Add(newHeader);
            ++i;
            if (i >= maxCount)
                break;

            ++startingPoint;
        }

        return headers.ToArray();
    }

    public bool TrySetNextBlocks(int maxCount, BlockDownloadContext context)
    {
        long? startingPoint = GetStartingPoint();
        if (startingPoint == null)
            return false;
        BlockInfo? beaconMainChainBlockInfo = GetBeaconMainChainBlockInfo(startingPoint.Value + 1);
        if (beaconMainChainBlockInfo is not {IsBeaconBody: true}) return false;

        int offset = 0;
        while (offset != context.NonEmptyBlockHashes.Count)
        {
            IReadOnlyList<Keccak> hashesToRequest = context.GetHashesByOffset(offset, maxCount);
            for (int i = 0; i < hashesToRequest.Count; i++)
            {
                Block? block = _blockTree.FindBlock(hashesToRequest[i], BlockTreeLookupOptions.None);
                if (block == null) continue;
                BlockBody blockBody = new(block.Transactions, block.Uncles);
                context.SetBody(i + offset, blockBody);
            }

            offset += hashesToRequest.Count;
        }

        return true;
    }

    private long? GetStartingPoint()
    {
        long startingPoint = _blockTree.BestKnownNumber + 1;
        bool foundBeaconBlock;

        BlockInfo? beaconMainChainBlock = GetBeaconMainChainBlockInfo(startingPoint);
        if (beaconMainChainBlock == null) return null;
        Keccak currentHash = beaconMainChainBlock.BlockHash;
        // in normal situation we will have one iteration of this loop, in some cases a few. Thanks to that we don't need to add extra pointer to manage forward syncing
        do
        {
            BlockHeader? header = _blockTree.FindHeader(currentHash!, BlockTreeLookupOptions.None);
            if (header == null)
            {
                if (_logger.IsTrace) _logger.Trace($"Header for number {startingPoint} was not found");
                return null;
            }
            
            BlockInfo blockInfo = (_blockTree.GetInfo( header.Number - 1, header.ParentHash!)).Info;
            foundBeaconBlock = blockInfo.IsBeaconInfo;
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Searching for starting point on level {startingPoint}. Header: {header.ToString(BlockHeader.Format.FullHashAndNumber)}, BlockInfo: {blockInfo?.ToString()}");
            --startingPoint;
            currentHash = header.ParentHash!;
            if (_syncConfig.FastSync && startingPoint <= _syncConfig.PivotNumberParsed)
            {
                if (_logger.IsTrace) _logger.Trace($"Reached syncConfig pivot. Starting point: {startingPoint}");
                break;
            }
        } while (foundBeaconBlock);

        return startingPoint;
    }

    private BlockInfo? GetBeaconMainChainBlockInfo(long startingPoint)
    {
        ChainLevelInfo? startingLevel = _blockTree.FindLevel(startingPoint);
        BlockInfo? beaconMainChainBlock = startingLevel?.BeaconMainChainBlock;
        if (beaconMainChainBlock == null)
        {
            if (_logger.IsTrace) _logger.Trace($"Beacon main chain block for number {startingPoint} was not found");
            return null;
        }

        return beaconMainChainBlock;
    }
}
