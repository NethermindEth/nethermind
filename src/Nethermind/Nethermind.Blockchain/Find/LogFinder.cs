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
using Nethermind.Logging;
using Nethermind.Store.Bloom;

namespace Nethermind.Blockchain.Find
{
    public class LogFinder : ILogFinder
    {
        private readonly IReceiptFinder _receiptFinder;
        private readonly IBloomStorage _bloomStorage;
        private readonly int _maxBlockDepth;
        private readonly IBlockFinder _blockFinder;
        private readonly ILogger _logger;

        public LogFinder(IBlockFinder blockFinder, IReceiptFinder receiptFinder, IBloomStorage bloomStorage, ILogManager logManager, int maxBlockDepth = 1000)
        {
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _bloomStorage = bloomStorage ?? throw new ArgumentNullException(nameof(bloomStorage));
            _logger = logManager?.GetClassLogger<LogFinder>() ?? throw new ArgumentNullException(nameof(logManager));
            _maxBlockDepth = maxBlockDepth;
        }

        public IEnumerable<FilterLog> FindLogs(LogFilter filter)
        {
            BlockHeader FindHeader(BlockParameter blockParameter, string name) => _blockFinder.FindHeader(blockParameter) ?? throw new ArgumentException(ILogFinder.NotFoundError, name);

            var toBlock = FindHeader(filter.ToBlock, nameof(filter.ToBlock));
            var fromBlock = FindHeader(filter.FromBlock, nameof(filter.FromBlock));

            if (fromBlock.Number > toBlock.Number && toBlock.Number != 0)
            {
                throw new ArgumentException("'From' block is later than 'to' block.");
            }

            return ShouldUseBloomDatabase(fromBlock, toBlock) && CanUseBloomDatabase(toBlock, fromBlock)
                ? FilterLogsWithBloomsIndex(filter, fromBlock, toBlock) 
                : FilterLogsIteratively(filter, fromBlock, toBlock);
        }

        private bool ShouldUseBloomDatabase(BlockHeader fromBlock, BlockHeader toBlock)
        {
            var blocksToSearch = toBlock.Number - fromBlock.Number + 1;
            return blocksToSearch > 1; // if we are searching only in 1 block skip bloom index altogether, this can be tweaked
        }

        private IEnumerable<FilterLog> FilterLogsWithBloomsIndex(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock)
        {
            Block FindBlock(long blockNumber)
            {
                var block = _blockFinder.FindBlock(blockNumber);
                if (block == null)
                {
                    if (_logger.IsError) _logger.Error($"Could not find block {blockNumber} in database. eth_getLogs will return incomplete results.");
                }
                return block;
            }

            var enumeration = _bloomStorage.GetBlooms(fromBlock.Number, toBlock.Number);
            foreach (var bloom in enumeration)
            {
                if (filter.Matches(bloom) && enumeration.TryGetBlockNumber(out var blockNumber))
                {
                    foreach (var filterLog in FindLogsInBlock(filter, FindBlock(blockNumber)))
                    {
                        yield return filterLog;
                    }
                }
            }
        }

        private bool CanUseBloomDatabase(BlockHeader toBlock, BlockHeader fromBlock) => _bloomStorage.ContainsRange(fromBlock.Number, toBlock.Number) && _blockFinder.IsMainChain(toBlock) && _blockFinder.IsMainChain(fromBlock);

        private IEnumerable<FilterLog> FilterLogsIteratively(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock)
        {
            int count = 0;

            while (count < _maxBlockDepth && toBlock.Number >= (fromBlock?.Number ?? long.MaxValue))
            {
                foreach (var filterLog in FindLogsInBlock(filter, toBlock))
                {
                    yield return filterLog;
                }

                if (!TryGetParentBlock(toBlock, out toBlock))
                {
                    break;
                }

                count++;
            }
        }

        private IEnumerable<FilterLog> FindLogsInBlock(LogFilter filter, BlockHeader block) =>
            filter.Matches(block.Bloom)
                ? FindLogsInBlock(filter, _blockFinder.FindBlock(block.Hash))
                : Enumerable.Empty<FilterLog>();

        private IEnumerable<FilterLog> FindLogsInBlock(LogFilter filter, Block block)
        {
            if (block != null)
            {
                var receipts = _receiptFinder.Get(block);
                long logIndexInBlock = 0;
                if (receipts != null)
                {
                    foreach (var receipt in receipts)
                    {
                        if (filter.Matches(receipt.Bloom))
                        {
                            for (var index = 0; index < receipt.Logs.Length; index++)
                            {
                                var log = receipt.Logs[index];
                                if (filter.Accepts(log))
                                {
                                    yield return new FilterLog(logIndexInBlock, index, receipt, log);
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
            }
        }

        private bool TryGetParentBlock(BlockHeader currentBlock, out BlockHeader parentHeader)
        {
            if (currentBlock.IsGenesis)
            {
                parentHeader = null;
                return false;
            }

            parentHeader = _blockFinder.FindParentHeader(currentBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return true;
        }
    }
}