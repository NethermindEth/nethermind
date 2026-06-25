// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Facade.Find
{
    public class LogFinder(
        IBlockFinder? blockFinder,
        IReceiptFinder? receiptFinder,
        IReceiptStorage? receiptStorage,
        ILogManager? logManager,
        IReceiptsRecovery? receiptsRecovery,
        int maxBlockDepth = 1000)
        : ILogFinder
    {
        private static int ParallelExecutions = 0;
        private static int ParallelLock = 0;

        private readonly IReceiptFinder _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
        private readonly IReceiptStorage _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        private readonly IReceiptsRecovery _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
        private readonly int _rpcConfigGetLogsThreads = Math.Max(1, Environment.ProcessorCount / 4);
        private readonly IBlockFinder _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        private readonly ILogger _logger = logManager?.GetClassLogger<LogFinder>() ?? throw new ArgumentNullException(nameof(logManager));

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default)
        {
            BlockHeader FindHeader(BlockParameter blockParameter, string name, bool headLimit) =>
                _blockFinder.FindHeader(blockParameter, headLimit) ?? throw new ResourceNotFoundException($"Block not found: {name} {blockParameter}");

            cancellationToken.ThrowIfCancellationRequested();
            BlockHeader toBlock = FindHeader(filter.ToBlock, nameof(filter.ToBlock), false);
            cancellationToken.ThrowIfCancellationRequested();
            BlockHeader fromBlock = filter.ToBlock == filter.FromBlock ?
                toBlock :
                FindHeader(filter.FromBlock, nameof(filter.FromBlock), false);

            return FindLogs(filter, fromBlock, toBlock, cancellationToken);
        }

        public virtual IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default)
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

            return FilterLogsIteratively(filter, fromBlock, toBlock, cancellationToken);
        }

        protected IEnumerable<FilterLog> FilterLogsInBlocksParallel(LogFilter filter, IEnumerable<ulong> blockNumbers, bool tryParallel = true, CancellationToken cancellationToken = default) =>
            RunParallel(blockNumbers,
                number => FindLogsInBlock(filter, FindHeaderOrLogError(number, cancellationToken), cancellationToken), tryParallel, cancellationToken);

        private IEnumerable<FilterLog> RunParallel<T>(IEnumerable<T> source, Func<T, IEnumerable<FilterLog>> worker, bool tryParallel, CancellationToken cancellationToken)
        {
            if (!tryParallel)
            {
                return source.SelectMany(worker);
            }

            static IEnumerable<T> ReleaseLockOnDispose(IEnumerable<T> source, bool runParallel, CancellationToken ct)
            {
                try
                {
                    foreach (T item in source)
                    {
                        yield return item;
                        ct.ThrowIfCancellationRequested();
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

            IEnumerable<T> wrapped = ReleaseLockOnDispose(source, canRunParallel, cancellationToken);

            if (canRunParallel)
            {
                if (_logger.IsTrace) _logger.Trace($"Allowing parallel eth_getLogs, already parallel executions: {parallelExecutions}.");
                wrapped = wrapped.AsParallel() // can yield big performance improvements
                    .AsOrdered() // we want to keep block order
                    .WithDegreeOfParallelism(_rpcConfigGetLogsThreads); // explicitly provide number of threads
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Not allowing parallel eth_getLogs, already parallel executions: {parallelExecutions}.");
            }

            return wrapped.SelectMany(worker);
        }

        private IEnumerable<FilterLog> FilterLogsIteratively(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken)
        {
            if (toBlock.Number < fromBlock.Number)
            {
                return [];
            }

            static IEnumerable<ulong> BlockNumbers(ulong from, ulong count)
            {
                for (ulong i = 0; i < count; i++) yield return from + i;
            }

            ulong rangeSize = Math.Min((ulong)maxBlockDepth, toBlock.Number - fromBlock.Number + 1);
            bool tryParallel = rangeSize >= (ulong)_rpcConfigGetLogsThreads;
            return FilterLogsInBlocksParallel(filter, BlockNumbers(fromBlock.Number, rangeSize), tryParallel, cancellationToken);
        }

        private IEnumerable<FilterLog> FindLogsInBlock(LogFilter filter, BlockHeader? block, CancellationToken cancellationToken) =>
            block is not null && filter.Matches(block.Bloom!)
                ? FindLogsInBlock(filter, block.Hash, block.Number, block.Timestamp, cancellationToken)
                : [];

        private IEnumerable<FilterLog> FindLogsInBlock(LogFilter filter, Hash256? blockHash, ulong blockNumber, ulong blockTimestamp, CancellationToken cancellationToken)
        {
            if (blockHash is not null)
            {
                return _receiptFinder.TryGetReceiptsIterator(blockNumber, blockHash, out ReceiptsIterator iterator)
                    ? FilterLogsInBlockLowMemoryAllocation(filter, ref iterator, blockTimestamp, cancellationToken)
                    : FilterLogsInBlockHighMemoryAllocation(filter, blockHash, blockNumber, blockTimestamp, cancellationToken);
            }

            return Array.Empty<FilterLog>();
        }

        private static IEnumerable<FilterLog> FilterLogsInBlockLowMemoryAllocation(LogFilter filter, ref ReceiptsIterator iterator, ulong blockTimestamp, CancellationToken cancellationToken)
        {
            List<FilterLog> logList = null;
            try
            {
                long logIndexInBlock = 0;
                while (iterator.TryGetNext(out TxReceiptStructRef receipt))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    LogEntriesIterator logsIterator = iterator.IterateLogs(receipt);
                    if (!iterator.CanDecodeBloom || filter.Matches(ref receipt.Bloom))
                    {
                        while (logsIterator.TryGetNext(out LogEntryStructRef log))
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (filter.Accepts(ref log))
                            {
                                // On CL workload, recovery happens about 70% of the time.
                                iterator.RecoverIfNeeded(ref receipt);

                                logList ??= [];
                                Hash256[] topics = log.Topics;

                                topics ??= iterator.DecodeTopics(new RlpReader(log.TopicsRlp));

                                logList.Add(new FilterLog(
                                    logIndexInBlock,
                                    receipt.BlockNumber,
                                    blockTimestamp,
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

        private IEnumerable<FilterLog> FilterLogsInBlockHighMemoryAllocation(LogFilter filter, Hash256 blockHash, ulong blockNumber, ulong blockTimestamp, CancellationToken cancellationToken)
        {
            TxReceipt[]? GetReceipts(Hash256 hash, ulong number)
            {
                bool canUseHash = _receiptFinder.CanGetReceiptsByHash(number);
                if (canUseHash)
                {
                    return _receiptFinder.Get(hash);
                }
                else
                {
                    Block block = _blockFinder.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                    return block is null ? null : _receiptFinder.Get(block);
                }
            }

            void RecoverReceiptsData(Hash256 hash, TxReceipt[] receipts)
            {
                if (_receiptsRecovery.NeedRecover(receipts))
                {
                    Block block = _blockFinder.FindBlock(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
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

            TxReceipt[] receipts = GetReceipts(blockHash, blockNumber);
            long logIndexInBlock = 0;
            if (receipts is not null)
            {
                for (int i = 0; i < receipts.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    TxReceipt receipt = receipts[i];

                    if (filter.Matches(receipt.Bloom))
                    {
                        for (int j = 0; j < receipt.Logs.Length; j++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            LogEntry log = receipt.Logs[j];
                            if (filter.Accepts(log))
                            {
                                RecoverReceiptsData(blockHash, receipts);
                                yield return new FilterLog(logIndexInBlock, receipt, log, blockTimestamp);
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

        protected BlockHeader? FindHeaderOrLogError(ulong blockNumber, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            BlockHeader? block = _blockFinder.FindHeader(blockNumber);
            if (block is null && _logger.IsError)
            {
                _logger.Error($"Could not find block {blockNumber} in database. eth_getLogs will return incomplete results.");
            }

            return block;
        }
    }
}
