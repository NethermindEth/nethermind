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

namespace Nethermind.Blockchain.Find
{
    public class LogFinder : ILogFinder
    {
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockFinder _blockFinder;
        private const long PendingBlockNumber = long.MaxValue;

        public LogFinder(IBlockFinder blockFinder, IReceiptStorage receiptStorage)
        {
            _receiptStorage = receiptStorage;
            _blockFinder = blockFinder;
        }

        public FilterLog[] FindLogs(LogFilter filter)
        {
            var toBlock = _blockFinder.GetBlock(filter.ToBlock);
            var fromBlock = _blockFinder.GetBlock(filter.FromBlock);
            List<FilterLog> results = new List<FilterLog>();

            while (toBlock.Number >= (fromBlock?.Number ?? long.MaxValue))
            {
                if (filter.Matches(toBlock.Bloom))
                {
                    FindLogsInBlock(filter, toBlock, results);
                }

                if (!TryGetParentBlock(toBlock, out toBlock))
                {
                    break;
                }
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
                    foreach (var log in receipt.Logs)
                    {
                        if (filter.Accepts(log))
                        {
                            results.Add(new FilterLog(logIndexInBlock, receipt, log));
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

        private bool TryGetParentBlock(Block currentBlock, out Block parentBlock)
        {
            if (currentBlock.IsGenesis)
            {
                parentBlock = null;
                return false;
            }
            else
            {
                parentBlock = _blockFinder.FindBlock(currentBlock.ParentHash);
                return true;
            }
        }
    }
}