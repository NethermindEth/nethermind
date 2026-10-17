// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Autofac.Features.AttributeFilters;

namespace Nethermind.Facade.Find
{
    public class LogFinder(
        IBlockFinder? blockFinder,
        [KeyFilter(IReceiptFinder.RegenerableKey)] IReceiptFinder? receiptFinder,
        IReceiptStorage? receiptStorage,
        ILogManager? logManager,
        IReceiptsRecovery? receiptsRecovery,
        ISpecProvider specProvider,
        IReceiptConfig? receiptConfig = null,
        IPrunedLogsRetention? prunedLogsRetention = null)
        : ILogFinder
    {
        // PLINQ's fixed setup cost (chunk buffers, thread-pool hops) outweighs scanning a few blocks per worker inline.
        private const int MinBlocksPerParallelWorker = 4;

        private static int ParallelExecutions = 0;
        private static int ParallelLock = 0;

        public static bool IsParallelScanSlotHeld => Volatile.Read(ref ParallelLock) != 0;

        private readonly IReceiptFinder _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
        private readonly IReceiptStorage _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        private readonly IReceiptsRecovery _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
        // With receipts derived from state, a body absent from disk is not absent from the node, so it cannot gate
        // availability.
        private readonly bool _bodiesOnDisk = receiptConfig?.DeriveFromState != true;
        private readonly int _rpcConfigGetLogsThreads = Math.Max(1, Environment.ProcessorCount / 4);
        private readonly IBlockFinder _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        private readonly ILogger _logger = logManager?.GetClassLogger<LogFinder>() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly Eip8116Schedule _eip8116 = new(specProvider);

        public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default)
        {
            (BlockHeader fromBlock, BlockHeader toBlock) = ResolveRange(_blockFinder, filter, cancellationToken);
            return FindLogs(filter, fromBlock, toBlock, cancellationToken);
        }

        /// <summary> Resolves a filter's block parameters into the headers bounding the scan. </summary>
        /// <exception cref="ResourceNotFoundException"> Either parameter does not resolve to a known block. </exception>
        internal static (BlockHeader FromBlock, BlockHeader ToBlock) ResolveRange(IBlockFinder blockFinder, LogFilter filter, CancellationToken cancellationToken)
        {
            BlockHeader FindHeader(BlockParameter blockParameter, string name, bool headLimit) =>
                blockFinder.FindHeader(blockParameter, headLimit) ?? throw new ResourceNotFoundException($"Block not found: {name} {blockParameter}");

            cancellationToken.ThrowIfCancellationRequested();
            BlockHeader toBlock = FindHeader(filter.ToBlock, nameof(filter.ToBlock), false);
            cancellationToken.ThrowIfCancellationRequested();
            BlockHeader fromBlock = filter.ToBlock == filter.FromBlock ?
                toBlock :
                FindHeader(filter.FromBlock, nameof(filter.FromBlock), false);

            return (fromBlock, toBlock);
        }

        public virtual IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (fromBlock.Number > toBlock.Number)
            {
                throw new ArgumentException($"From block {fromBlock.Number} is later than to block {toBlock.Number}.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (_bodiesOnDisk)
            {
                bool fromMissing = fromBlock.Number != 0 && fromBlock.ReceiptsRoot != Keccak.EmptyTreeHash && !_receiptStorage.HasBlock(fromBlock.Number, fromBlock.Hash!);
                cancellationToken.ThrowIfCancellationRequested();
                bool toMissing = toBlock.Number != 0 && toBlock.ReceiptsRoot != Keccak.EmptyTreeHash && !_receiptStorage.HasBlock(toBlock.Number, toBlock.Hash!);

                if ((fromMissing || toMissing) && !RetainsLogsForFilter(filter, fromBlock.Number, toBlock.Number))
                {
                    throw new ResourceNotFoundException(fromMissing
                        ? $"Receipt not available for From block {fromBlock.Number}."
                        : $"Receipt not available for To block {toBlock.Number}.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();

            return new FoundLogs(
                FilterLogsIteratively<BlockFilterLogs, FilterLog>(filter, fromBlock, toBlock, cancellationToken),
                FilterLogsIteratively<BlockSerializedLogs, SerializedLogs>(filter, fromBlock, toBlock, cancellationToken));
        }

        protected bool RetainsLogsForFilter(LogFilter filter, ulong fromBlock, ulong toBlock) =>
            prunedLogsRetention is not null
            && filter.AddressFilter.Addresses.Count != 0
            && prunedLogsRetention.RetainsLogsFor(filter.AddressFilter.Addresses, fromBlock, toBlock);

        private protected IEnumerable<TLog> FilterLogsInBlocksParallel<TBlockLogs, TLog>(LogFilter filter, IEnumerable<ulong> blockNumbers, bool tryParallel = true, CancellationToken cancellationToken = default)
            where TBlockLogs : struct, IBlockLogs<TBlockLogs, TLog> =>
            RunParallel(blockNumbers,
                number => FindLogsInBlock<TBlockLogs, TLog>(filter, FindHeaderOrLogError(number, cancellationToken), cancellationToken), tryParallel, cancellationToken);

        private IEnumerable<TLog> RunParallel<T, TLog>(IEnumerable<T> source, Func<T, IEnumerable<TLog>> worker, bool tryParallel, CancellationToken cancellationToken)
        {
            if (!tryParallel)
            {
                return source.SelectMany(worker);
            }

            // we want to support one parallel eth_getLogs call for maximum performance
            // we don't want support more than one eth_getLogs call so we don't starve CPU and threads
            return RunParallelLazy(source, worker, cancellationToken);
        }

        // Must stay a lazy iterator: the lock is acquired on the first MoveNext and released in the finally,
        // so acquire and release share one enumerator lifetime. An eager version would leak the lock when the
        // result is never enumerated.
        private IEnumerable<TLog> RunParallelLazy<T, TLog>(IEnumerable<T> source, Func<T, IEnumerable<TLog>> worker, CancellationToken cancellationToken)
        {
            bool canRunParallel = Interlocked.CompareExchange(ref ParallelLock, 1, 0) == 0;
            int parallelExecutions = Interlocked.Increment(ref ParallelExecutions) - 1;
            try
            {
                if (_logger.IsTrace) _logger.Trace(canRunParallel
                    ? $"Allowing parallel eth_getLogs, already parallel executions: {parallelExecutions}."
                    : $"Not allowing parallel eth_getLogs, already parallel executions: {parallelExecutions}.");

                IEnumerable<T> wrapped = canRunParallel
                    ? source.AsParallel().AsOrdered().WithDegreeOfParallelism(_rpcConfigGetLogsThreads)
                    : source;

                using IEnumerator<TLog> enumerator = wrapped.SelectMany(worker).GetEnumerator();
                while (true)
                {
                    try
                    {
                        if (!enumerator.MoveNext()) break;
                    }
                    catch (AggregateException e) when (FirstMappable(e) is { } mappable)
                    {
                        // PLINQ wraps worker exceptions, but the RPC layer maps bare types onto error codes — a
                        // multi-block range must answer the same way a single-block range does.
                        ExceptionDispatchInfo.Capture(mappable).Throw();
                        throw; // unreachable; tells the compiler the catch does not complete
                    }

                    yield return enumerator.Current;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                if (canRunParallel)
                {
                    Interlocked.CompareExchange(ref ParallelLock, 0, 1);
                }
                Interlocked.Decrement(ref ParallelExecutions);
            }
        }

        private static Exception? FirstMappable(AggregateException e)
        {
            // Unavailability wins over transient shapes (it stays true after a retry); an unexpected fault wins
            // over both, so the aggregate falls through to the generic handler — the only site that logs it.
            Exception? notFound = null;
            Exception? transient = null;
            foreach (Exception inner in e.Flatten().InnerExceptions)
            {
                switch (inner)
                {
                    case ResourceNotFoundException:
                        notFound ??= inner;
                        break;
                    case ConcurrencyLimitReachedException or OperationCanceledException:
                        transient ??= inner;
                        break;
                    default:
                        return null;
                }
            }
            return notFound ?? transient;
        }

        private IEnumerable<TLog> FilterLogsIteratively<TBlockLogs, TLog>(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock, CancellationToken cancellationToken)
            where TBlockLogs : struct, IBlockLogs<TBlockLogs, TLog>
        {
            static IEnumerable<ulong> BlockNumbers(ulong from, ulong count)
            {
                for (ulong i = 0; i < count; i++) yield return from + i;
            }

            ulong rangeSize = toBlock.Number - fromBlock.Number + 1;
            bool tryParallel = ScansInParallel(rangeSize, _rpcConfigGetLogsThreads);
            return FilterLogsInBlocksParallel<TBlockLogs, TLog>(filter, BlockNumbers(fromBlock.Number, rangeSize), tryParallel, cancellationToken);
        }

        internal static bool ScansInParallel(ulong rangeSize, int workers) => rangeSize >= (ulong)(workers * MinBlocksPerParallelWorker);

        private IEnumerable<TLog> FindLogsInBlock<TBlockLogs, TLog>(LogFilter filter, BlockHeader? block, CancellationToken cancellationToken)
            where TBlockLogs : struct, IBlockLogs<TBlockLogs, TLog> =>
            block is not null && filter.Matches(block.Bloom!)
                ? FindLogsInBlock<TBlockLogs, TLog>(filter, block.Hash, block.Number, block.Timestamp, cancellationToken)
                : [];

        private IEnumerable<TLog> FindLogsInBlock<TBlockLogs, TLog>(LogFilter filter, Hash256? blockHash, ulong blockNumber, ulong blockTimestamp, CancellationToken cancellationToken)
            where TBlockLogs : struct, IBlockLogs<TBlockLogs, TLog>
        {
            if (blockHash is not null)
            {
                // EIP-8116: logIndex counts within the receipt.
                bool logIndexPerReceipt = _eip8116.IsEnabled(blockNumber, blockTimestamp);
                return _receiptFinder.TryGetReceiptsIterator(blockNumber, blockHash, out ReceiptsIterator iterator)
                    ? FilterLogsInBlockLowMemoryAllocation<TBlockLogs, TLog>(filter, ref iterator, blockHash, blockNumber, blockTimestamp, logIndexPerReceipt, cancellationToken)
                    : TBlockLogs.FromBlock(FilterLogsInBlockHighMemoryAllocation(filter, blockHash, blockNumber, blockTimestamp, logIndexPerReceipt, cancellationToken));
            }

            return Array.Empty<TLog>();
        }

        private static IEnumerable<TLog> FilterLogsInBlockLowMemoryAllocation<TBlockLogs, TLog>(LogFilter filter, ref ReceiptsIterator iterator, Hash256 blockHash, ulong blockNumber, ulong blockTimestamp, bool logIndexPerReceipt, CancellationToken cancellationToken)
            where TBlockLogs : struct, IBlockLogs<TBlockLogs, TLog>
        {
            TBlockLogs logs = new();
            try
            {
                long logIndexInBlock = 0;
                while (iterator.TryGetNext(out TxReceiptStructRef receipt))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (logIndexPerReceipt) logIndexInBlock = 0;

                    Hash256? txHash = null;

                    LogEntriesIterator logsIterator = iterator.IterateLogs(receipt);
                    if (!iterator.CanDecodeBloom || receipt.Bloom.Bytes.IsEmpty || filter.Matches(ref receipt.Bloom))
                    {
                        while (logsIterator.TryGetNext(out LogEntryStructRef log))
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (filter.Accepts(ref log))
                            {
                                // On CL workload, recovery happens about 70% of the time.
                                iterator.RecoverLogFieldsIfNeeded(ref receipt, out Hash256? recoveredTransactionHash);

                                Hash256[] topics = log.Topics;

                                topics ??= iterator.DecodeTopics(new RlpReader(log.TopicsRlp));

                                logs.Add(logIndexInBlock, blockNumber, blockTimestamp, blockHash, receipt, recoveredTransactionHash, ref txHash, log, topics);
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
            catch
            {
                logs.Dispose();
                throw;
            }
            finally
            {
                // Confusingly, the `using` statement causes the recovery context to be null during the dispose.
                iterator.Dispose();
            }

            return logs.Complete();
        }

        private IEnumerable<FilterLog> FilterLogsInBlockHighMemoryAllocation(LogFilter filter, Hash256 blockHash, ulong blockNumber, ulong blockTimestamp, bool logIndexPerReceipt, CancellationToken cancellationToken)
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
                    if (logIndexPerReceipt) logIndexInBlock = 0;

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

        /// <summary>Collects the logs of one block that the filter accepts, in the shape <typeparamref name="TLog"/>.</summary>
        private protected interface IBlockLogs<TSelf, TLog> : IDisposable where TSelf : struct, IBlockLogs<TSelf, TLog>
        {
            void Add(long logIndex, ulong blockNumber, ulong blockTimestamp, Hash256 blockHash, in TxReceiptStructRef receipt,
                Hash256? recoveredTransactionHash, ref Hash256? transactionHash, in LogEntryStructRef log, Hash256[] topics);

            IEnumerable<TLog> Complete();

            /// <summary>Converts the logs of any number of blocks, a block at a time where they allow it.</summary>
            static abstract IEnumerable<TLog> From(IEnumerable<FilterLog> logs);

            /// <summary>Converts the logs of a single block.</summary>
            static abstract IEnumerable<TLog> FromBlock(IEnumerable<FilterLog> blockLogs);
        }

        private protected struct BlockFilterLogs : IBlockLogs<BlockFilterLogs, FilterLog>
        {
            private List<FilterLog>? _logs;

            public void Add(long logIndex, ulong blockNumber, ulong blockTimestamp, Hash256 blockHash, in TxReceiptStructRef receipt,
                Hash256? recoveredTransactionHash, ref Hash256? transactionHash, in LogEntryStructRef log, Hash256[] topics)
            {
                byte[] data = log.DataLength == 0 ? [] : new byte[log.DataLength];
                log.CopyDataTo(data);

                (_logs ??= []).Add(new FilterLog(
                    logIndex,
                    blockNumber,
                    blockTimestamp,
                    blockHash,
                    receipt.Index,
                    transactionHash ??= recoveredTransactionHash ?? receipt.TxHash.ToCommitment(),
                    log.Address.ToAddress(),
                    data,
                    topics));
            }

            public readonly IEnumerable<FilterLog> Complete() => _logs ?? (IEnumerable<FilterLog>)[];

            public readonly void Dispose() { }

            public static IEnumerable<FilterLog> From(IEnumerable<FilterLog> logs) => logs;

            public static IEnumerable<FilterLog> FromBlock(IEnumerable<FilterLog> blockLogs) => blockLogs;
        }

        private protected struct BlockSerializedLogs : IBlockLogs<BlockSerializedLogs, SerializedLogs>
        {
            private SerializedLogs? _logs;

            public void Add(long logIndex, ulong blockNumber, ulong blockTimestamp, Hash256 blockHash, in TxReceiptStructRef receipt,
                Hash256? recoveredTransactionHash, ref Hash256? transactionHash, in LogEntryStructRef log, Hash256[] topics) =>
                (_logs ??= new SerializedLogs()).Add(
                    logIndex,
                    blockNumber,
                    blockTimestamp,
                    blockHash,
                    receipt.Index,
                    recoveredTransactionHash is null ? receipt.TxHash.Bytes : recoveredTransactionHash.Bytes,
                    log,
                    topics);

            public readonly IEnumerable<SerializedLogs> Complete() => _logs is null ? [] : [_logs];

            public readonly void Dispose() => _logs?.Dispose();

            public static IEnumerable<SerializedLogs> From(IEnumerable<FilterLog> logs) => FoundLogs.Serialize(logs);

            public static IEnumerable<SerializedLogs> FromBlock(IEnumerable<FilterLog> blockLogs)
            {
                SerializedLogs? serialized = null;
                try
                {
                    foreach (FilterLog log in blockLogs)
                    {
                        (serialized ??= new SerializedLogs()).Add(log);
                    }
                }
                catch
                {
                    serialized?.Dispose();
                    throw;
                }

                return serialized is null ? [] : [serialized];
            }
        }
    }
}
