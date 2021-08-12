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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Db.Blooms;

namespace Nethermind.Blockchain.Find
{
    public class LogFinder : ILogFinder
    {
        private static int ParallelExecutions = 0;
        private static int ParallelLock = 0;
        
        private readonly IReceiptFinder _receiptFinder;
        private readonly IBloomStorage _bloomStorage;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private readonly int _maxBlockDepth;
        private readonly int _rpcConfigGetLogsThreads;
        private readonly IBlockFinder _blockFinder;
        private readonly ILogger _logger;
        
        public LogFinder(
            IBlockFinder? blockFinder, 
            IReceiptFinder? receiptFinder, 
            IBloomStorage? bloomStorage, 
            ILogManager? logManager, 
            IReceiptsRecovery? receiptsRecovery, 
            int maxBlockDepth = 1000)
        {
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _bloomStorage = bloomStorage ?? throw new ArgumentNullException(nameof(bloomStorage));
            _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
            _logger = logManager?.GetClassLogger<LogFinder>() ?? throw new ArgumentNullException(nameof(logManager));
            _maxBlockDepth = maxBlockDepth;
            _rpcConfigGetLogsThreads = Math.Max(1, Environment.ProcessorCount / 4);
        }

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default)
        {
            BlockHeader FindHeader(BlockParameter blockParameter, string name, bool headLimit) => 
                _blockFinder.FindHeader(blockParameter, headLimit) ?? throw new ArgumentException(ILogFinder.NotFoundError, name);

            cancellationToken.ThrowIfCancellationRequested();
            var toBlock = FindHeader(filter.ToBlock, nameof(filter.ToBlock), true);
            cancellationToken.ThrowIfCancellationRequested();
            var fromBlock = FindHeader(filter.FromBlock, nameof(filter.FromBlock), false);

            if (fromBlock.Number > toBlock.Number && toBlock.Number != 0)
            {
                throw new ArgumentException("'From' block is later than 'to' block.");
            }
            
            cancellationToken.ThrowIfCancellationRequested();

            bool shouldUseBloom = ShouldUseBloomDatabase(fromBlock, toBlock);
            bool canUseBloom = CanUseBloomDatabase(toBlock, fromBlock);
            bool useBloom = shouldUseBloom && canUseBloom;
            return useBloom
                ? FilterLogsWithBloomsIndex(filter, fromBlock, toBlock, cancellationToken)
                : FilterLogsIteratively(filter, fromBlock, toBlock, cancellationToken);
        }

        private bool ShouldUseBloomDatabase(BlockHeader fromBlock, BlockHeader toBlock)
        {
            var blocksToSearch = toBlock.Number - fromBlock.Number + 1;
            return blocksToSearch > 1; // if we are searching only in 1 block skip bloom index altogether, this can be tweaked
        }

        private IEnumerable<FilterLog> FilterLogsWithBloomsIndex(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken)
        {
            Keccak FindBlockHash(long blockNumber, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var blockHash = _blockFinder.FindBlockHash(blockNumber);
                if (blockHash == null)
                {
                    if (_logger.IsError) _logger.Error($"Could not find block {blockNumber} in database. eth_getLogs will return incomplete results.");
                }

                return blockHash;
            }

            IEnumerable<long> FilterBlocks(LogFilter f, long @from, long to, bool runParallel, CancellationToken token)
            {
                try
                {
                    var enumeration = _bloomStorage.GetBlooms(from, to);
                    foreach (var bloom in enumeration)
                    {
                        token.ThrowIfCancellationRequested();
                        if (f.Matches(bloom) && enumeration.TryGetBlockNumber(out var blockNumber))
                        {
                            yield return blockNumber;
                        }
                    }
                }
                finally
                {
                    if (runParallel)
                    {
                        Interlocked.CompareExchange(ref ParallelLock, 0, 1);
                    }
                    Interlocked.Decrement(ref ParallelExecutions);
                }
            }

            // we want to support one parallel eth_getLogs call for maximum performance
            // we don't want support more than one eth_getLogs call so we don't starve CPU and threads
            int parallelLock = Interlocked.CompareExchange(ref ParallelLock, 1, 0);
            int parallelExecutions = Interlocked.Increment(ref ParallelExecutions) - 1;
            bool canRunParallel = parallelLock == 0;
            
            IEnumerable<long> filterBlocks = FilterBlocks(filter, fromBlock.Number, toBlock.Number, canRunParallel, cancellationToken);

            if (canRunParallel)
            {
                if (_logger.IsTrace) _logger.Trace($"Allowing parallel eth_getLogs, already parallel executions: {parallelExecutions}.");
                filterBlocks = filterBlocks.AsParallel() // can yield big performance improvements
                    .AsOrdered() // we want to keep block order
                    .WithDegreeOfParallelism(_rpcConfigGetLogsThreads); // explicitly provide number of threads
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Not allowing parallel eth_getLogs, already parallel executions: {parallelExecutions}.");
            }
            
            return filterBlocks
                .SelectMany(blockNumber => FindLogsInBlock(filter, FindBlockHash(blockNumber, cancellationToken), blockNumber, cancellationToken));
        }

        private bool CanUseBloomDatabase(BlockHeader toBlock, BlockHeader fromBlock)
        {
            // method is designed for convenient debugging

            bool containsRange = _bloomStorage.ContainsRange(fromBlock.Number, toBlock.Number);
            if (!containsRange)
            {
                return false;
            }

            bool toIsOnMainChain = _blockFinder.IsMainChain(toBlock);
            if (!toIsOnMainChain)
            {
                return false;
            }

            bool fromIsOnMainChain = _blockFinder.IsMainChain(fromBlock);
            if (!fromIsOnMainChain)
            {
                return false;
            }

            return true;
        }

        private IEnumerable<FilterLog> FilterLogsIteratively(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken)
        {
            int count = 0;
            while (count < _maxBlockDepth && toBlock.Number >= (fromBlock?.Number ?? long.MaxValue))
            {
                foreach (var filterLog in FindLogsInBlock(filter, toBlock, cancellationToken))
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

        private IEnumerable<FilterLog> FindLogsInBlock(LogFilter filter, BlockHeader block, CancellationToken cancellationToken) =>
            filter.Matches(block.Bloom)
                ? FindLogsInBlock(filter, block.Hash, block.Number, cancellationToken)
                : Enumerable.Empty<FilterLog>();

        private IEnumerable<FilterLog> FindLogsInBlock(LogFilter filter, Keccak blockHash, long blockNumber, CancellationToken cancellationToken)
        {
            if (blockHash != null)
            {
                return _receiptFinder.TryGetReceiptsIterator(blockNumber, blockHash, out var iterator)
                    ? FilterLogsInBlockLowMemoryAllocation(filter, ref iterator, cancellationToken)
                    : FilterLogsInBlockHighMemoryAllocation(filter, blockHash, blockNumber, cancellationToken);
            }

            return Array.Empty<FilterLog>();
        }

        private static IEnumerable<FilterLog> FilterLogsInBlockLowMemoryAllocation(LogFilter filter, ref ReceiptsIterator iterator, CancellationToken cancellationToken)
        {
            List<FilterLog> logList = null;
            using (iterator)
            {
                long logIndexInBlock = 0;
                while (iterator.TryGetNext(out var receipt))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    LogEntriesIterator logsIterator = receipt.Logs == null ? new LogEntriesIterator(receipt.LogsRlp) : new LogEntriesIterator(receipt.Logs);
                    if (filter.Matches(ref receipt.Bloom))
                    {
                        while (logsIterator.TryGetNext(out var log))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            
                            if (filter.Accepts(ref log))
                            {
                                logList ??= new List<FilterLog>();
                                Keccak[] topics = log.Topics;

                                if (topics == null)
                                {
                                    var topicsValueDecoderContext = new Rlp.ValueDecoderContext(log.TopicsRlp);
                                    topics = KeccakDecoder.Instance.DecodeArray(ref topicsValueDecoderContext);
                                }

                                logList.Add(new FilterLog(
                                    logIndexInBlock,
                                    logsIterator.Index,
                                    receipt.BlockNumber,
                                    receipt.BlockHash.ToKeccak(),
                                    receipt.Index,
                                    receipt.TxHash.ToKeccak(),
                                    log.LoggersAddress.ToAddress(),
                                    log.Data.ToArray(),
                                    topics));
                            }

                            logIndexInBlock++;
                        }
                    }
                    else
                    {
                        while (logsIterator.TrySkipNext())
                        {
                            logIndexInBlock++;
                        }
                    }
                }
            }

            return logList ?? (IEnumerable<FilterLog>) Array.Empty<FilterLog>();
        }

        private IEnumerable<FilterLog> FilterLogsInBlockHighMemoryAllocation(LogFilter filter, Keccak blockHash, long blockNumber, CancellationToken cancellationToken)
        {
            TxReceipt[] GetReceipts(Keccak hash, long number)
            {
                var canUseHash = _receiptFinder.CanGetReceiptsByHash(number);
                if (canUseHash)
                {
                    return _receiptFinder.Get(hash);
                }
                else
                {
                    var block = _blockFinder.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                    return block == null ? null : _receiptFinder.Get(block);
                }
            }

            void RecoverReceiptsData(Keccak hash, TxReceipt[] receipts)
            {
                if (_receiptsRecovery.NeedRecover(receipts))
                {
                    var block = _blockFinder.FindBlock(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                    if (block != null)
                    {
                        _receiptsRecovery.TryRecover(block, receipts);
                    }
                }
            }
            
            cancellationToken.ThrowIfCancellationRequested();

            var receipts = GetReceipts(blockHash, blockNumber);
            long logIndexInBlock = 0;
            if (receipts != null)
            {
                for (var i = 0; i < receipts.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    var receipt = receipts[i];

                    if (filter.Matches(receipt.Bloom))
                    {
                        for (var j = 0; j < receipt.Logs.Length; j++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            
                            var log = receipt.Logs[j];
                            if (filter.Accepts(log))
                            {
                                RecoverReceiptsData(blockHash, receipts);
                                yield return new FilterLog(logIndexInBlock, j, receipt, log);
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
