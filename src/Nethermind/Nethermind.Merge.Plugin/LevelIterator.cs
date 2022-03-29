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

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin;

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
        BlockHeader?[] headersToDownload = new BlockHeader[maxCount];
        int i = 0;
        long currentNumber = _blockTree.BestSuggestedBody?.Number + 1 ?? 0;
        while (i < maxCount)
        {
            ChainLevelInfo? level = _blockTree.FindLevel(currentNumber);
            if (level == null)
            {
                if (_logger.IsInfo) _logger.Info($"Level {currentNumber} not found");
                break;
            }
                
            BlockHeader? newHeader = _blockTree.FindHeader(level.MainChainBlock.BlockHash);
            headersToDownload[i] = newHeader;
            ++i;
            ++currentNumber;
        }

        return headersToDownload;
    }
}
