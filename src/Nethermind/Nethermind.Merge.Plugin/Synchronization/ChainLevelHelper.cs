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
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Synchronization;

public interface IChainLevelHelper
{
    BlockHeader[] GetNextHeaders(int maxCount);

    Block[] GetNextBlocks(int maxCount);
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

    public BlockHeader[] GetNextHeaders(int maxCount)
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

            if ((beaconMainChainBlock.Metadata & (BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody)) != 0)
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

    public Block[] GetNextBlocks(int maxCount)
    {
        long? startingPoint = GetStartingPoint();
        if (startingPoint == null)
            return null;
        List<Block> blocks = new(maxCount);
        int i = 0;
        while (i < maxCount)
        {
            ChainLevelInfo? level = _blockTree.FindLevel(startingPoint!.Value);
            BlockInfo? beaconMainChainBlock = level?.BeaconMainChainBlock;
            if (level == null || beaconMainChainBlock == null)
            {
                if (_logger.IsTrace) _logger.Trace($"ChainLevelHelper.GetNextBlocks - level {startingPoint} not found");
                break;
            }
            
            Block? newBlock = _blockTree.FindBlock(beaconMainChainBlock.BlockHash);
            if (newBlock == null)
            {
                if (_logger.IsTrace) _logger.Trace($"ChainLevelHelper - block {startingPoint} not found");
                continue;
            }

            newBlock.Header.TotalDifficulty = beaconMainChainBlock.TotalDifficulty == 0
                ? null
                : beaconMainChainBlock.TotalDifficulty;
            if (_logger.IsTrace)
                _logger.Trace(
                    $"ChainLevelHelper - A new block block {newBlock.ToString(Block.Format.FullHashAndNumber)}");
            blocks.Add(newBlock);
            ++i;
            if (i >= maxCount)
                break;

            ++startingPoint;
        }

        return blocks.ToArray();
    }

    private long? GetStartingPoint()
    {
        long startingPoint = _blockTree.BestKnownNumber + 1;
        bool parentBlockExists;
        // in normal situation we will have one iteration of this loop, in some cases a few. Thanks to that we don't need to add extra pointer to manage forward syncing
        do
        {
            BlockHeader? header = _blockTree.FindHeader(startingPoint, BlockTreeLookupOptions.None);
            if (header == null)
            {
                if (_logger.IsTrace) _logger.Trace($"Header for number {startingPoint} was not found");
                return null;
            }

            Block? block = _blockTree.FindBlock(header!.ParentHash ?? header.CalculateHash());
            parentBlockExists = block != null;
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Searching for starting point on level {startingPoint}. Header: {header.ToString(BlockHeader.Format.FullHashAndNumber)}, Block: {block?.ToString(Block.Format.FullHashAndNumber)}");
            --startingPoint;
            if (_syncConfig.FastSync && startingPoint <= _syncConfig.PivotNumberParsed)
            {
                if (_logger.IsTrace) _logger.Trace($"Reached syncConfig pivot. Starting point: {startingPoint}");
                break;
            }
        } while (!parentBlockExists);

        return startingPoint;
    }
}
