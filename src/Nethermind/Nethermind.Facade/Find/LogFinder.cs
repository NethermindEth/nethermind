// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        // The parallel scan's fixed setup cost (thread-pool hops) outweighs scanning a few blocks per worker inline.
        private const int MinBlocksPerParallelWorker = 4;

        // Chunks double each round of workers up to this cap: small first rounds keep short ranges balanced, larger
        // later ones amortize the task hop that every chunk costs.
        private const int MaxChunkDoublings = 4;

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
                bool fromMissing = ReceiptsMissing(fromBlock);
                cancellationToken.ThrowIfCancellationRequested();
                bool toMissing = ReceiptsMissing(toBlock);

                if (fromMissing && !WasPruned(fromBlock))
                {
                    // As in geth, a single block whose header bloom misses the filter has no logs to read.
                    if (fromBlock.Hash == toBlock.Hash && !filter.Matches(fromBlock.Bloom!))
                    {
                        return new FoundLogs([], []);
                    }

                    throw new BlockNotProcessedException(fromBlock.Number);
                }

                if (toMissing && !WasPruned(toBlock))
                {
                    throw new BlockNotProcessedException(toBlock.Number);
                }

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

        /// <summary>Throws when the receipts of <paramref name="header"/> are missing because the block is not processed yet.</summary>
        /// <exception cref="BlockNotProcessedException">The block is not processed yet.</exception>
        protected void ThrowIfNotProcessed(BlockHeader header)
        {
            if (_bodiesOnDisk && ReceiptsMissing(header) && !WasPruned(header))
            {
                throw new BlockNotProcessedException(header.Number);
            }
        }

        private bool ReceiptsMissing(BlockHeader header) =>
            header.Number != 0 && header.ReceiptsRoot != Keccak.EmptyTreeHash && !_receiptStorage.HasBlock(header.Number, header.Hash!);

        /// <remarks>
        /// A block ahead of the processed head or off the main chain may not be processed yet, so its receipts can
        /// still appear.
        /// </remarks>
        private bool WasPruned(BlockHeader header) =>
            _blockFinder.Head?.Number >= header.Number && _blockFinder.IsMainChain(header);

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

                IEnumerable<TLog> logs = canRunParallel
                    ? ScanInOrderedChunks(source, worker)
                    : source.SelectMany(worker);

                foreach (TLog log in logs)
                {
                    yield return log;
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

        /// <summary>Scans <paramref name="source"/> on worker tasks and yields the logs in source order.</summary>
        /// <remarks>
        /// At most one chunk per worker is in flight, so memory is bounded by that window rather than the range.
        /// Logs scanned but never yielded are disposed when they are <see cref="IDisposable"/>, as <see cref="SerializedLogs"/> hold pooled buffers.
        /// </remarks>
        internal IEnumerable<TLog> ScanInOrderedChunks<T, TLog>(IEnumerable<T> source, Func<T, IEnumerable<TLog>> worker)
        {
            Queue<Task<List<TLog>>> pending = new(_rpcConfigGetLogsThreads);
            using CancellationTokenSource abandoned = new();
            using IEnumerator<T> items = source.GetEnumerator();
            List<TLog>? current = null;
            int delivered = 0;
            try
            {
                bool sourceDrained = false;
                int chunksStarted = 0;
                while (true)
                {
                    while (!sourceDrained && pending.Count < _rpcConfigGetLogsThreads)
                    {
                        int chunkSize = MinBlocksPerParallelWorker << Math.Min(MaxChunkDoublings, chunksStarted++ / _rpcConfigGetLogsThreads);
                        List<T> chunk = new(chunkSize);
                        while (chunk.Count < chunkSize && items.MoveNext()) chunk.Add(items.Current);
                        sourceDrained = chunk.Count < chunkSize;
                        if (chunk.Count != 0) pending.Enqueue(Task.Run(() => ScanChunk(chunk, worker, abandoned.Token)));
                    }

                    if (!pending.TryDequeue(out Task<List<TLog>>? next)) yield break;

                    current = next.GetAwaiter().GetResult();
                    for (delivered = 0; delivered < current.Count;)
                    {
                        yield return current[delivered++];
                    }
                }
            }
            finally
            {
                // Workers must not outlive the parallel slot.
                abandoned.Cancel();
                ((Task)Task.WhenAll(pending)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();

                if (current is not null) DisposeFrom(current, delivered);
                foreach (Task<List<TLog>> task in pending)
                {
                    if (task.IsCompletedSuccessfully) DisposeFrom(task.Result, 0);
                }
            }
        }

        private static List<TLog> ScanChunk<T, TLog>(List<T> chunk, Func<T, IEnumerable<TLog>> worker, CancellationToken abandoned)
        {
            List<TLog> logs = [];
            try
            {
                foreach (T item in chunk)
                {
                    if (abandoned.IsCancellationRequested) break;
                    logs.AddRange(worker(item));
                }
            }
            catch
            {
                DisposeFrom(logs, 0);
                throw;
            }

            return logs;
        }

        private static void DisposeFrom<TLog>(List<TLog> logs, int start)
        {
            for (int i = start; i < logs.Count; i++)
            {
                if (logs[i] is IDisposable disposable) disposable.Dispose();
            }
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
            Func<ReadOnlySpan<byte>, bool> bloomMatches = bloom =>
            {
                if (bloom.IsEmpty) return true;
                BloomStructRef bloomRef = new(bloom);
                return filter.Matches(ref bloomRef);
            };

            // A block-hash filter can target a processed block that is not canonical, so a lookup by number misses it.
            if (fromBlock.Hash == toBlock.Hash)
            {
                return RunParallel([fromBlock], header => FindLogsInBlock<TBlockLogs, TLog>(filter, header, cancellationToken), tryParallel: false, cancellationToken);
            }

            return RunParallel(BlockNumbers(fromBlock.Number, rangeSize),
                number => FindHeaderIfBloomMatchesOrLogError(number, bloomMatches, cancellationToken) is { } header
                    ? FindLogsInBlock<TBlockLogs, TLog>(filter, header.Hash, header.Number, header.Timestamp, cancellationToken)
                    : [],
                tryParallel, cancellationToken);
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
            bool receiptsRecovered = false;
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
                                if (!receiptsRecovered)
                                {
                                    RecoverReceiptsData(blockHash, receipts);
                                    receiptsRecovered = true;
                                }

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
            if (block is null) LogMissingBlock(blockNumber);

            return block;
        }

        private BlockHeader? FindHeaderIfBloomMatchesOrLogError(ulong blockNumber, Func<ReadOnlySpan<byte>, bool> bloomMatches, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            BlockHeader? block = _blockFinder.FindCanonicalHeaderIfBloomMatches(blockNumber, bloomMatches, out bool found);
            if (!found) LogMissingBlock(blockNumber);

            return block;
        }

        private void LogMissingBlock(ulong blockNumber)
        {
            if (_logger.IsError) _logger.Error($"Could not find block {blockNumber} in database. eth_getLogs will return incomplete results.");
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
