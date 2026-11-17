// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.FluentRlp;
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

                // Sliced retention guarantees matching logs on the canonical chain by height, not receipts
                // for a particular non-canonical block hash. A processed orphan with missing receipts must
                // fail closed even when the same address is retained at this height.
                if ((fromMissing || toMissing)
                    && ((fromMissing && !_blockFinder.IsMainChain(fromBlock))
                        || (toMissing && !_blockFinder.IsMainChain(toBlock))
                        || !RetainsLogsForFilter(filter, fromBlock.Number, toBlock.Number)))
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
        /// Missing receipts on an already processed non-canonical block are unavailable history, not proof that
        /// the block still awaits execution. Reorgs change canonical membership without clearing WasProcessed.
        /// </remarks>
        internal static bool WasPruned(IBlockFinder blockFinder, BlockHeader header) =>
            (blockFinder.Head?.Number >= header.Number && blockFinder.IsMainChain(header))
            || blockFinder.GetInfo(header.Number, header.Hash!).Info?.WasProcessed == true;

        private bool WasPruned(BlockHeader header) => WasPruned(_blockFinder, header);

        protected bool RetainsLogsForFilter(LogFilter filter, ulong fromBlock, ulong toBlock) =>
            prunedLogsRetention is not null
            && filter.AddressFilter.Addresses.Count != 0
            && prunedLogsRetention.RetainsLogsFor(filter.AddressFilter.Addresses, fromBlock, toBlock);

        private protected IEnumerable<TLog> FilterLogsInBlocksParallel<TBlockLogs, TLog>(LogFilter filter, IEnumerable<ulong> blockNumbers, bool tryParallel = true, CancellationToken cancellationToken = default)
            where TBlockLogs : struct, IBlockLogs<TBlockLogs, TLog> =>
            RunParallel(blockNumbers,
                number => FindLogsInBlock<TBlockLogs, TLog>(filter, FindHeaderOrLogError(number, cancellationToken), cancellationToken), tryParallel, cancellationToken);

        private IEnumerable<TLog> RunParallel<T, TLog>(IEnumerable<T> source, Func<T, IEnumerable<TLog>> worker, bool tryParallel, CancellationToken cancellationToken) =>
            RunParallelInBatches(source, _ => worker, tryParallel, cancellationToken);

        /// <param name="workerFor">
        /// Gives the worker of a batch of consecutive items, which it can prepare at once. The batch is pooled, so it must not
        /// be kept after the call.
        /// </param>
        private IEnumerable<TLog> RunParallelInBatches<T, TLog>(IEnumerable<T> source, Func<ReadOnlyMemory<T>, Func<T, IEnumerable<TLog>>> workerFor, bool tryParallel, CancellationToken cancellationToken)
        {
            if (!tryParallel)
            {
                return InBatches(source, workerFor);
            }

            // we want to support one parallel eth_getLogs call for maximum performance
            // we don't want support more than one eth_getLogs call so we don't starve CPU and threads
            return RunParallelLazy(source, workerFor, cancellationToken);
        }

        private static IEnumerable<TLog> InBatches<T, TLog>(IEnumerable<T> source, Func<ReadOnlyMemory<T>, Func<T, IEnumerable<TLog>>> workerFor)
        {
            const int batchSize = MinBlocksPerParallelWorker << MaxChunkDoublings;
            using IEnumerator<T> items = source.GetEnumerator();
            using ArrayPoolList<T> batch = new(batchSize);
            while (true)
            {
                batch.Clear();
                while (batch.Count < batchSize && items.MoveNext()) batch.Add(items.Current);
                if (batch.Count == 0) yield break;

                Func<T, IEnumerable<TLog>> worker = workerFor(batch.AsMemory());
                foreach (T item in batch)
                {
                    foreach (TLog log in worker(item)) yield return log;
                }
            }
        }

        // Must stay a lazy iterator: the lock is acquired on the first MoveNext and released in the finally,
        // so acquire and release share one enumerator lifetime. An eager version would leak the lock when the
        // result is never enumerated.
        private IEnumerable<TLog> RunParallelLazy<T, TLog>(IEnumerable<T> source, Func<ReadOnlyMemory<T>, Func<T, IEnumerable<TLog>>> workerFor, CancellationToken cancellationToken)
        {
            bool canRunParallel = Interlocked.CompareExchange(ref ParallelLock, 1, 0) == 0;
            int parallelExecutions = Interlocked.Increment(ref ParallelExecutions) - 1;
            try
            {
                if (_logger.IsTrace) _logger.Trace(canRunParallel
                    ? $"Allowing parallel eth_getLogs, already parallel executions: {parallelExecutions}."
                    : $"Not allowing parallel eth_getLogs, already parallel executions: {parallelExecutions}.");

                IEnumerable<TLog> logs = canRunParallel
                    ? ScanInOrderedBatches(source, workerFor)
                    : InBatches(source, workerFor);

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
        internal IEnumerable<TLog> ScanInOrderedChunks<T, TLog>(IEnumerable<T> source, Func<T, IEnumerable<TLog>> worker) =>
            ScanInOrderedBatches(source, _ => worker);

        /// <inheritdoc cref="ScanInOrderedChunks{T, TLog}(IEnumerable{T}, Func{T, IEnumerable{TLog}})"/>
        /// <param name="workerFor">Gives the worker of a chunk, which it can prepare at once.</param>
        private IEnumerable<TLog> ScanInOrderedBatches<T, TLog>(IEnumerable<T> source, Func<ReadOnlyMemory<T>, Func<T, IEnumerable<TLog>>> workerFor)
        {
            Queue<Task<List<TLog>?>> pending = new(_rpcConfigGetLogsThreads);
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
                        T[] chunk = ArrayPool<T>.Shared.Rent(chunkSize);
                        int count = 0;
                        while (count < chunkSize && items.MoveNext()) chunk[count++] = items.Current;
                        sourceDrained = count < chunkSize;
                        // Task.Run with a state object, which saves a closure and a delegate per chunk.
                        if (count != 0) pending.Enqueue(Task.Factory.StartNew(ScanChunk<T, TLog>, (new ArraySegment<T>(chunk, 0, count), workerFor, abandoned.Token), CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
                        else ArrayPool<T>.Shared.Return(chunk);
                    }

                    if (!pending.TryDequeue(out Task<List<TLog>?>? next)) yield break;

                    current = next.GetAwaiter().GetResult();
                    for (delivered = 0; current is not null && delivered < current.Count;)
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
                foreach (Task<List<TLog>?> task in pending)
                {
                    if (task.IsCompletedSuccessfully) DisposeFrom(task.Result, 0);
                }
            }
        }

        private static List<TLog>? ScanChunk<T, TLog>(object? state)
        {
            (ArraySegment<T> chunk, Func<ReadOnlyMemory<T>, Func<T, IEnumerable<TLog>>> workerFor, CancellationToken abandoned) =
                ((ArraySegment<T>, Func<ReadOnlyMemory<T>, Func<T, IEnumerable<TLog>>>, CancellationToken))state!;
            // Stays null while the blocks have no logs, as most chunks of a selective filter do.
            List<TLog>? logs = null;
            try
            {
                Func<T, IEnumerable<TLog>> worker = workerFor(chunk);
                foreach (T item in chunk)
                {
                    if (abandoned.IsCancellationRequested) break;
                    IEnumerable<TLog> found = worker(item);
                    if (!found.TryGetNonEnumeratedCount(out int count) || count != 0) (logs ??= []).AddRange(found);
                }
            }
            catch
            {
                DisposeFrom(logs, 0);
                throw;
            }

            ArrayPool<T>.Shared.Return(chunk.Array!, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            return logs;
        }

        private static void DisposeFrom<TLog>(List<TLog>? logs, int start)
        {
            if (logs is null) return;

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

            return RunParallelInBatches(BlockNumbers(fromBlock.Number, rangeSize),
                numbers => FindLogsInBlocksIfBloomMatches<TBlockLogs, TLog>(filter, numbers, bloomMatches, cancellationToken),
                tryParallel, cancellationToken);
        }

        /// <summary>
        /// Finds the canonical headers of <paramref name="blockNumbers"/> whose bloom matches in one batch, logs the
        /// missing blocks, and returns the worker that finds the logs of one of these blocks.
        /// </summary>
        /// <param name="blockNumbers">Consecutive block numbers.</param>
        private Func<ulong, IEnumerable<TLog>> FindLogsInBlocksIfBloomMatches<TBlockLogs, TLog>(LogFilter filter, ReadOnlyMemory<ulong> blockNumbers,
            Func<ReadOnlySpan<byte>, bool> bloomMatches, CancellationToken cancellationToken)
            where TBlockLogs : struct, IBlockLogs<TBlockLogs, TLog>
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong firstNumber = blockNumbers.Span[0];
            BlockHeader[]? matches = null;
            using (ArrayPoolSpan<BlockHeader?> headers = new(blockNumbers.Length))
            using (ArrayPoolSpan<bool> found = new(blockNumbers.Length))
            {
                _blockFinder.FindCanonicalHeadersIfBloomMatches(firstNumber, bloomMatches, headers.AsMemory(), found.AsMemory());

                int matchCount = 0;
                for (int i = 0; i < headers.Length; i++)
                {
                    if (!found[i]) LogMissingBlock(firstNumber + (ulong)i);
                    if (headers[i] is not null) matchCount++;
                }

                if (matchCount != 0)
                {
                    matches = new BlockHeader[matchCount];
                    matchCount = 0;
                    for (int i = 0; i < headers.Length; i++)
                    {
                        if (headers[i] is { } header) matches[matchCount++] = header;
                    }
                }

                // The rented array goes back to a shared pool, which must not keep the headers alive.
                ((Span<BlockHeader?>)headers).Clear();
            }

            return matches is null ? static _ => [] : FindLogsInMatches<TBlockLogs, TLog>(filter, matches, cancellationToken);
        }

        // Separate from the batch read so that a chunk with no match does not allocate the closure.
        private Func<ulong, IEnumerable<TLog>> FindLogsInMatches<TBlockLogs, TLog>(LogFilter filter, BlockHeader[] matches, CancellationToken cancellationToken)
            where TBlockLogs : struct, IBlockLogs<TBlockLogs, TLog> =>
            number =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (BlockHeader header in matches)
                {
                    if (header.Number == number) return FindLogsInBlock<TBlockLogs, TLog>(filter, header.Hash, header.Number, header.Timestamp, cancellationToken);
                }

                return [];
            };

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

                                logs.Add(logIndexInBlock, blockNumber, blockTimestamp, blockHash, receipt, recoveredTransactionHash, ref txHash, log, in iterator);
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

        private void LogMissingBlock(ulong blockNumber)
        {
            if (_logger.IsError) _logger.Error($"Could not find block {blockNumber} in database. eth_getLogs will return incomplete results.");
        }

        /// <summary>Collects the logs of one block that the filter accepts, in the shape <typeparamref name="TLog"/>.</summary>
        private protected interface IBlockLogs<TSelf, TLog> : IDisposable where TSelf : struct, IBlockLogs<TSelf, TLog>
        {
            void Add(long logIndex, ulong blockNumber, ulong blockTimestamp, Hash256 blockHash, in TxReceiptStructRef receipt,
                Hash256? recoveredTransactionHash, ref Hash256? transactionHash, in LogEntryStructRef log, in ReceiptsIterator iterator);

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
                Hash256? recoveredTransactionHash, ref Hash256? transactionHash, in LogEntryStructRef log, in ReceiptsIterator iterator)
            {
                byte[] data = log.DataLength == 0 ? [] : new byte[log.DataLength];
                log.CopyDataTo(data);
                Hash256[] topics = log.Topics ?? iterator.DecodeTopics(new RlpReader(log.TopicsRlp));

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
                Hash256? recoveredTransactionHash, ref Hash256? transactionHash, in LogEntryStructRef log, in ReceiptsIterator iterator) =>
                (_logs ??= new SerializedLogs()).Add(
                    logIndex,
                    blockNumber,
                    blockTimestamp,
                    blockHash,
                    receipt.Index,
                    recoveredTransactionHash is null ? receipt.TxHash.Bytes : recoveredTransactionHash.Bytes,
                    log,
                    iterator);

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
