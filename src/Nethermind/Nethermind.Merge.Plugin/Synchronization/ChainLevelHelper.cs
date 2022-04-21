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
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Synchronization;

public interface IChainLevelHelper
{
    BlockHeader[] GetNextHeaders(int maxCount);
}

public class ChainLevelHelper : IChainLevelHelper
{
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;

    public ChainLevelHelper(
        IBlockTree blockTree,
        ILogManager logManager)
    {
        _blockTree = blockTree;
        _logger = logManager.GetClassLogger();
    }
    
    public BlockHeader[] GetNextHeaders(int maxCount)
    {
        List<BlockHeader> headersToDownload = new(maxCount);
        int i = 0;
        long currentNumber = _blockTree.BestKnownNumber;
        while (i < maxCount)
        {
            ChainLevelInfo? level = _blockTree.FindLevel(currentNumber);
            if (level == null)
            {
                if (_logger.IsTrace) _logger.Trace($"ChainLevelHelper - level {currentNumber} not found");
                break;
            }

            for (int j = 0; j < level.BlockInfos.Length; ++j)
            {
                BlockHeader? newHeader = _blockTree.FindHeader(level.BlockInfos[j].BlockHash);
                if (newHeader == null)
                {
                    if (_logger.IsTrace) _logger.Trace($"ChainLevelHelper - header {currentNumber} not found");
                    continue;
                }
                
                if (_logger.IsTrace) _logger.Trace($"ChainLevelHelper - A new block header {newHeader.ToString(BlockHeader.Format.FullHashAndNumber)}");
                headersToDownload.Add(newHeader);
                ++i;
                if (i >= maxCount)
                    break;
            }
            
            ++currentNumber;
        }

        return headersToDownload.ToArray();
    }
}
