// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Facade.Filters;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Facade.Find
{
    public class LogFinder : ILogFinder
    {
        private static int ParallelExecutions = 0;
        private static int ParallelLock = 0;

        private readonly IReceiptFinder _receiptFinder;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBloomStorage _bloomStorage;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private readonly int _maxBlockDepth;
        private readonly int _rpcConfigGetLogsThreads;
        private readonly IBlockFinder _blockFinder;
        private readonly ILogger _logger;
        private readonly ILogIndexStorage? _logIndexStorage;

        public LogFinder(IBlockFinder? blockFinder,
            IReceiptFinder? receiptFinder,
            IReceiptStorage? receiptStorage,
            IBloomStorage? bloomStorage,
            ILogManager? logManager,
            IReceiptsRecovery? receiptsRecovery,
            ILogIndexStorage? logIndexStorage,
            int maxBlockDepth = 1000)
        {
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage)); ;
            _bloomStorage = bloomStorage ?? throw new ArgumentNullException(nameof(bloomStorage));
            _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
            _logIndexStorage = logIndexStorage;
            _logger = logManager?.GetClassLogger<LogFinder>() ?? throw new ArgumentNullException(nameof(logManager));
            _maxBlockDepth = maxBlockDepth;
            _rpcConfigGetLogsThreads = Math.Max(1, Environment.ProcessorCount / 4);
        }

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlockHeader toBlock = FindHeader(filter.ToBlock, nameof(filter.ToBlock));
            cancellationToken.ThrowIfCancellationRequested();
            BlockHeader fromBlock = filter.ToBlock == filter.FromBlock ?
                toBlock :
                FindHeader(filter.FromBlock, nameof(filter.FromBlock));

            return FindLogs(filter, fromBlock, toBlock, cancellationToken);
        }

        private BlockHeader FindHeader(BlockParameter blockParameter, string name) =>
            _blockFinder.FindHeader(blockParameter) ?? throw new ResourceNotFoundException($"Block not found: {name} {blockParameter}");

        private BlockHeader FindHeader(int number) => FindHeader(new(number), $"{number}");

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (fromBlock.Number > toBlock.Number && toBlock.Number != 0)
            {
                throw new ArgumentException($"From block {fromBlock.Number} is later than to block {toBlock.Number}.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (fromBlock.Number != 0 && fromBlock.ReceiptsRoot != Keccak.EmptyTreeHash && !_receiptStorage.HasBlock(fromBlock.Number, fromBlock.Hash!))
            {
                throw new ResourceNotFoundException($"Receipt not available for From block {fromBlock.Number}.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (toBlock.Number != 0 && toBlock.ReceiptsRoot != Keccak.EmptyTreeHash && !_receiptStorage.HasBlock(toBlock.Number, toBlock.Hash!))
            {
                throw new ResourceNotFoundException($"Receipt not available for To block {toBlock.Number}.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (CanUseLogIndex(filter, fromBlock, toBlock) is not { } indexRange)
                return FilterLogsWithoutIndex(filter, fromBlock, toBlock, cancellationToken);

            // Combine results from regular scanning and index
            IEnumerable<FilterLog>? result = [];

            if (indexRange.from > fromBlock.Number)
                result = result.Concat(FilterLogsWithoutIndex(filter, fromBlock, FindHeader(indexRange.from - 1), cancellationToken));

            cancellationToken.ThrowIfCancellationRequested();

            result = result.Concat(FilterLogsUsingIndex(filter, FindHeader(indexRange.from), FindHeader(indexRange.to), cancellationToken));

            cancellationToken.ThrowIfCancellationRequested();

            if (indexRange.to < toBlock.Number)
                result = result.Concat(FilterLogsWithoutIndex(filter, FindHeader(indexRange.to + 1), toBlock, cancellationToken));

            return result;
        }

        private IEnumerable<FilterLog> FilterLogsWithoutIndex(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock,
            CancellationToken cancellationToken)
        {
            bool shouldUseBloom = ShouldUseBloomDatabase(fromBlock, toBlock);
            bool canUseBloom = CanUseBloomDatabase(toBlock, fromBlock);
            bool useBloom = shouldUseBloom && canUseBloom;
            return useBloom
                ? FilterLogsWithBloomsIndex(filter, fromBlock, toBlock, cancellationToken)
                : FilterLogsIteratively(filter, fromBlock, toBlock, cancellationToken);
        }

        private static bool ShouldUseBloomDatabase(BlockHeader fromBlock, BlockHeader toBlock)
        {
            var blocksToSearch = toBlock.Number - fromBlock.Number + 1;
            return blocksToSearch > 1; // if we are searching only in 1 block skip bloom index altogether, this can be tweaked
        }

        private IEnumerable<FilterLog> FilterLogsWithBloomsIndex(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken)
        {
            Hash256? FindBlockHash(long blockNumber, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var blockHash = _blockFinder.FindBlockHash(blockNumber);
                if (blockHash is null)
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

        private (int from, int to)? CanUseLogIndex(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock)
        {
            if (!filter.UseIndex || _logIndexStorage == null || filter.AcceptsAnyBlock)
                return null;

            if (_logIndexStorage.GetMinBlockNumber() is not { } indexFrom || _logIndexStorage.GetMaxBlockNumber() is not { } indexTo)
                return null;

            return (
                Math.Max((int)fromBlock.Number, indexFrom),
                Math.Min((int)toBlock.Number, indexTo)
            );
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

        private IEnumerable<int> GetBlockNumbersFor(LogFilter filter, long fromBlock, long toBlock, CancellationToken cancellationToken)
        {
            if (_logIndexStorage == null)
                throw new InvalidOperationException("Log index storage is not provided.");

            ConcurrentDictionary<Address, List<int>> byAddress = null;
            if (filter.AddressFilter.Address is { } address)
            {
                byAddress = new() { [address] = null };
            }
            else if (filter.AddressFilter.Addresses is { Count: > 0 } addresses)
            {
                byAddress = new();
                byAddress.AddRange(addresses.Select(a => KeyValuePair.Create(a.Value, (List<int>)null)));
            }

            ConcurrentDictionary<Hash256, List<int>> byTopic = null;
            foreach (Hash256 topic in filter.TopicsFilter.Topics)
            {
                byTopic ??= new();
                byTopic[topic] = null;
            }

            Enumerable.Empty<object>()
                .Union(byAddress?.Keys ?? Enumerable.Empty<Address>())
                .Union(byTopic?.Keys ?? Enumerable.Empty<Hash256>())
                .AsParallel() // TODO utilize canRunParallel
                .ForAll(x =>
                {
                    if (x is Address addr)
                        byAddress![addr] = _logIndexStorage.GetBlockNumbersFor(addr, (int)fromBlock, (int)toBlock).ToList();
                    if (x is Hash256 tpc)
                        byTopic![tpc] = _logIndexStorage.GetBlockNumbersFor(tpc, (int)fromBlock, (int)toBlock).ToList();
                });

            if (byTopic is null) // TODO: avoid ToList call when single value
                return AscListHelper.UnionAll(byAddress?.Values ?? []);

            List<int> blockNumbers = filter.TopicsFilter.FilterBlockNumbers(byTopic);

            if (byAddress is null)
                return blockNumbers;

            blockNumbers = AscListHelper.IntersectAll(byAddress.Values.Concat([blockNumbers]));

            return blockNumbers;
        }

        private IEnumerable<FilterLog> FilterLogsUsingIndex(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock,
            CancellationToken cancellationToken)
        {
            // TODO: merge common code with FilterLogsWithBloomsIndex

            Hash256 FindBlockHash(long blockNumber, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var blockHash = _blockFinder.FindBlockHash(blockNumber);
                if (blockHash is null)
                {
                    if (_logger.IsError)
                        _logger.Error($"Could not find block {blockNumber} in database. eth_getLogs will return incomplete results.");
                }

                return blockHash;
            }

            IEnumerable<long> FilterBlocks(LogFilter f, long from, long to, bool runParallel, CancellationToken token)
            {
                try
                {
                    foreach (var blockNumber in GetBlockNumbersFor(f, from, to, token))
                    {
                        token.ThrowIfCancellationRequested();

                        yield return blockNumber;

                        // TODO: is this needed?
                        // if (++count >= _maxBlockDepth)
                        //     break;
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

        private IEnumerable<FilterLog> FilterLogsIteratively(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken)
        {
            int count = 0;
            while (count < _maxBlockDepth && fromBlock.Number <= (toBlock?.Number ?? fromBlock.Number))
            {
                foreach (var filterLog in FindLogsInBlock(filter, fromBlock, cancellationToken))
                {
                    yield return filterLog;
                }

                fromBlock = _blockFinder.FindHeader(fromBlock.Number + 1);
                if (fromBlock is null) break;

                count++;
            }
        }

        private IEnumerable<FilterLog> FindLogsInBlock(LogFilter filter, BlockHeader block, CancellationToken cancellationToken) =>
            filter.Matches(block.Bloom)
                ? FindLogsInBlock(filter, block.Hash, block.Number, cancellationToken)
                : [];

        private IEnumerable<FilterLog> FindLogsInBlock(LogFilter filter, Hash256? blockHash, long blockNumber, CancellationToken cancellationToken)
        {
            if (blockHash is not null)
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
            try
            {
                long logIndexInBlock = 0;
                while (iterator.TryGetNext(out var receipt))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    LogEntriesIterator logsIterator = iterator.IterateLogs(receipt);
                    if (!iterator.CanDecodeBloom || filter.Matches(ref receipt.Bloom))
                    {
                        while (logsIterator.TryGetNext(out var log))
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (filter.Accepts(ref log))
                            {
                                // On CL workload, recovery happens about 70% of the time.
                                iterator.RecoverIfNeeded(ref receipt);

                                logList ??= new List<FilterLog>();
                                Hash256[] topics = log.Topics;

                                topics ??= iterator.DecodeTopics(new Rlp.ValueDecoderContext(log.TopicsRlp));

                                logList.Add(new FilterLog(
                                    logIndexInBlock,
                                    receipt.BlockNumber,
                                    receipt.BlockHash.ToCommitment(),
                                    receipt.Index,
                                    receipt.TxHash.ToCommitment(),
                                    log.Address.ToAddress(),
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
            finally
            {
                // Confusingly, the `using` statement causes the recovery context to be null during the dispose.
                iterator.Dispose();
            }

            return logList ?? (IEnumerable<FilterLog>)[];
        }

        private IEnumerable<FilterLog> FilterLogsInBlockHighMemoryAllocation(LogFilter filter, Hash256 blockHash, long blockNumber, CancellationToken cancellationToken)
        {
            TxReceipt[]? GetReceipts(Hash256 hash, long number)
            {
                var canUseHash = _receiptFinder.CanGetReceiptsByHash(number);
                if (canUseHash)
                {
                    return _receiptFinder.Get(hash);
                }
                else
                {
                    var block = _blockFinder.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                    return block is null ? null : _receiptFinder.Get(block);
                }
            }

            void RecoverReceiptsData(Hash256 hash, TxReceipt[] receipts)
            {
                if (_receiptsRecovery.NeedRecover(receipts))
                {
                    var block = _blockFinder.FindBlock(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                    if (block is not null)
                    {
                        if (_receiptsRecovery.TryRecover(block, receipts) == ReceiptsRecoveryResult.NeedReinsert)
                        {
                            _receiptStorage.Insert(block, receipts);
                        }
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            var receipts = GetReceipts(blockHash, blockNumber);
            long logIndexInBlock = 0;
            if (receipts is not null)
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
                                yield return new FilterLog(logIndexInBlock, receipt, log);
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
}
