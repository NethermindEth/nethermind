//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Find
{
    public class LogFinder : ILogFinder
    {
        private readonly IReceiptStorage _receiptStorage;
        private readonly int _maxBlockDepth;
        private readonly IBlockFinder _blockFinder;
        
        public LogFinder(IBlockFinder blockFinder, IReceiptStorage receiptStorage, int maxBlockDepth = 1000)
        {
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _maxBlockDepth = maxBlockDepth;
        }

        public FilterLog[] FindLogs(LogFilter filter)
        {
            int count = 0;
            var block = _blockFinder.GetHeader(filter.ToBlock);
            if (block is null)
            {
                return Array.Empty<FilterLog>();
            }
            
            var fromBlock = _blockFinder.GetHeader(filter.FromBlock);
            List<FilterLog> results = new List<FilterLog>();

            while (count < _maxBlockDepth && block.Number >= (fromBlock?.Number ?? long.MaxValue))
            {
                if (filter.Matches(block.Bloom))
                {
                    FindLogsInBlock(filter, _blockFinder.FindBlock(block.Hash), results);
                }

                if (!TryGetParentBlock(block, out block))
                {
                    break;
                }

                count++;
            }

            return results.ToArray();
        }

        private void FindLogsInBlock(LogFilter filter, Block currentBlock, List<FilterLog> results)
        {
            var receipts = _receiptStorage.FindForBlock(currentBlock);
            long logIndexInBlock = 0;
            foreach (var receipt in receipts)
            {
                if (receipt == null)
                {
                    continue;
                }

                if (filter.Matches(receipt.Bloom))
                {
                    for (var index = 0; index < receipt.Logs.Length; index++)
                    {
                        var log = receipt.Logs[index];
                        if (filter.Accepts(log))
                        {
                            results.Add(new FilterLog(logIndexInBlock, index, receipt, log));
                        }

                        logIndexInBlock++;
                    }
                }
                else
                {
                    logIndexInBlock += receipt.Logs.Length;
                }
            }
        }

        private bool TryGetParentBlock(BlockHeader currentBlock, out BlockHeader parentBlock)
        {
            if (currentBlock.IsGenesis)
            {
                parentBlock = null;
                return false;
            }
            else
            {
                parentBlock = _blockFinder.FindHeader(currentBlock.ParentHash);
                return true;
            }
        }
    }
}