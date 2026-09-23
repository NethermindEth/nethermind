// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Facade.Filters
{
    /// <summary>
    /// Serves <c>eth_getFilterChanges</c> for installed filters.
    /// </summary>
    /// <remarks>
    /// Log and block filters do not buffer results. Processed blocks and reorg removals are appended once to a
    /// shared log that keeps their receipts, each filter holds only a cursor into it, and results are matched when
    /// the filter is polled. Memory is therefore independent of the number of filters and of how many logs they
    /// match; the log keeps the blocks no live filter has polled yet, capped at <see cref="MaxRetainedBlocks"/>.
    /// A filter that falls further behind is removed, as if it had timed out.
    /// </remarks>
    public sealed class FilterManager
    {
        private const int MaxRetainedBlocks = 1024;

        private readonly ConcurrentDictionary<int, ConcurrentQueue<Option<Hash256>>> _pendingTransactions =
            new();

        private readonly Lock _eventsLock = new();
        private readonly List<BlockEvent> _events = [];
        private readonly Dictionary<int, long> _cursors = [];
        private long _firstSequence;

        private Hash256? _lastBlockHash;
        private readonly FilterStore _filterStore;
        private readonly ILogger _logger;

        public FilterManager(
            FilterStore filterStore,
            IMainProcessingContext mainProcessingContext,
            ITxPool txPool,
            IReceiptMonitor receiptMonitor,
            ILogManager logManager)
        {
            _filterStore = filterStore ?? throw new ArgumentNullException(nameof(filterStore));
            txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            ArgumentNullException.ThrowIfNull(receiptMonitor);
            _logger = logManager?.GetClassLogger<FilterManager>() ?? throw new ArgumentNullException(nameof(logManager));
            mainProcessingContext.BranchProcessor.BlockProcessed += OnBlockProcessed;
            receiptMonitor.ReceiptsInserted += OnReceiptsInserted;
            _filterStore.FilterRemoved += OnFilterRemoved;
            txPool.NewPending += OnNewPendingTransaction;
            txPool.RemovedPending += OnRemovedPendingTransaction;
        }

        private void OnFilterRemoved(object sender, FilterEventArgs e)
        {
            int id = e.FilterId;
            if (_pendingTransactions.TryRemove(id, out _)) return;

            lock (_eventsLock)
            {
                _cursors.Remove(id);
            }
        }

        private void OnBlockProcessed(object sender, BlockProcessedEventArgs e)
        {
            Block block = e.Block;
            Hash256 blockHash = block.Hash ?? throw new InvalidOperationException("Cannot filter on blocks without calculated hashes");
            _lastBlockHash = blockHash;
            Append(new BlockEvent(blockHash, block.Timestamp, block.Header.Bloom, e.TxReceipts, Removed: false));
        }

        private void OnReceiptsInserted(object? sender, ReceiptsEventArgs e)
        {
            if (!e.WasRemoved || e.TxReceipts is null || e.TxReceipts.Length == 0)
            {
                return;
            }

            // The stored receipts are not guaranteed to carry blooms, so a removal is never skipped by bloom.
            Append(new BlockEvent(e.BlockHeader.Hash!, e.BlockHeader.Timestamp, null, e.TxReceipts, Removed: true));
        }

        /// <summary>
        /// Appends a block to the shared log and drops the blocks every live filter has already polled.
        /// </summary>
        /// <remarks>
        /// Log and block filters that have no cursor yet start at this block, so a filter sees every block
        /// processed after it was installed. Cursors of filters that are gone are dropped here too.
        /// </remarks>
        private void Append(BlockEvent blockEvent)
        {
            using ArrayPoolListRef<int> expired = new(0);
            lock (_eventsLock)
            {
                long sequence = _firstSequence + _events.Count;
                _events.Add(blockEvent);

                foreach (FilterBase filter in _filterStore.GetFilters<FilterBase>())
                {
                    if (filter is LogFilter or BlockFilter)
                    {
                        _cursors.TryAdd(filter.Id, sequence);
                    }
                }

                long tail = sequence + 1 - MaxRetainedBlocks;
                long oldestCursor = sequence + 1;
                using ArrayPoolListRef<int> removed = new(0);
                foreach (KeyValuePair<int, long> cursor in _cursors)
                {
                    if (!_filterStore.FilterExists(cursor.Key))
                    {
                        removed.Add(cursor.Key);
                    }
                    else if (cursor.Value < tail)
                    {
                        expired.Add(cursor.Key);
                    }
                    else
                    {
                        oldestCursor = Math.Min(oldestCursor, cursor.Value);
                    }
                }

                foreach (int id in removed.AsSpan())
                {
                    _cursors.Remove(id);
                }

                int dropped = (int)(Math.Max(oldestCursor, tail) - _firstSequence);
                if (dropped > 0)
                {
                    _events.RemoveRange(0, dropped);
                    _firstSequence += dropped;
                }
            }

            foreach (int id in expired.AsSpan())
            {
                if (_logger.IsDebug) _logger.Debug($"Removed filter {id}, it was not polled for {MaxRetainedBlocks} blocks.");
                _filterStore.RemoveFilter(id);
            }
        }

        /// <summary>
        /// Returns the blocks the filter has not polled yet, or <c>null</c> when no block arrived since it was installed.
        /// </summary>
        private BlockEvent[]? GetEvents(int filterId, bool advance)
        {
            lock (_eventsLock)
            {
                if (!_cursors.TryGetValue(filterId, out long cursor))
                {
                    return null;
                }

                long next = _firstSequence + _events.Count;
                if (advance)
                {
                    _cursors[filterId] = next;
                }

                int start = (int)Math.Max(0, cursor - _firstSequence);
                return CollectionsMarshal.AsSpan(_events)[start..].ToArray();
            }
        }

        private void OnNewPendingTransaction(object sender, TxPool.TxEventArgs e)
        {
            IEnumerable<PendingTransactionFilter> filters = _filterStore.GetFilters<PendingTransactionFilter>();
            foreach (PendingTransactionFilter filter in filters)
            {
                int filterId = filter.Id;
                ConcurrentQueue<Option<Hash256>> transactions = _pendingTransactions.GetOrAdd(filterId, static _ => new ConcurrentQueue<Option<Hash256>>());
                transactions.Enqueue(new Option<Hash256>(e.Transaction.Hash));
                if (_logger.IsTrace) _logger.Trace($"Filter with id: {filterId} contains {transactions.Count} transactions.");
            }
        }

        private void OnRemovedPendingTransaction(object sender, TxPool.TxEventArgs e)
        {
            IEnumerable<PendingTransactionFilter> filters = _filterStore.GetFilters<PendingTransactionFilter>();

            foreach (PendingTransactionFilter filter in filters)
            {
                int filterId = filter.Id;
                if (!_pendingTransactions.TryGetValue(filterId, out ConcurrentQueue<Option<Hash256>>? transactions))
                    continue;

                // Scan the queue and mark the matching item as removed.
                foreach (Option<Hash256> option in transactions)
                {
                    if (!option.IsRemoved && option.Value == e.Transaction.Hash)
                    {
                        option.MarkRemoved();
                        if (_logger.IsTrace) _logger.Trace($"Filter with id: {filterId}: transaction {e.Transaction.Hash} marked as removed.");
                    }
                }
            }
        }

        public FilterLog[] GetLogs(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            return GetLogs(filterId, advance: false);
        }

        public Hash256[] GetBlocksHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            BlockEvent[]? events = GetEvents(filterId, advance: false);
            return events is null ? [] : GetBlockHashes(events);
        }

        [Todo("Truffle sends transaction first and then polls so we hack it here for now")]
        public Hash256[] PollBlockHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            BlockEvent[]? events = GetEvents(filterId, advance: true);
            if (events is null)
            {
                if (_lastBlockHash is not null)
                {
                    Hash256[] hackedResult = { _lastBlockHash }; // truffle hack
                    _lastBlockHash = null;
                    return hackedResult;
                }

                return [];
            }

            return GetBlockHashes(events);
        }

        public FilterLog[] PollLogs(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            return GetLogs(filterId, advance: true);
        }

        public Hash256[] PollPendingTransactionHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            if (!_pendingTransactions.TryGetValue(filterId, out ConcurrentQueue<Option<Hash256>>? pendingTransactions))
                return [];

            using ArrayPoolListRef<Hash256> result = new(pendingTransactions.Count);
            while (pendingTransactions.TryDequeue(out Option<Hash256>? option))
            {
                if (!option.IsRemoved)
                {
                    result.Add(option.Value);
                }
            }
            return result.ToArray();
        }

        private static Hash256[] GetBlockHashes(BlockEvent[] events)
        {
            using ArrayPoolListRef<Hash256> result = new(events.Length);
            foreach (BlockEvent blockEvent in events)
            {
                if (!blockEvent.Removed)
                {
                    result.Add(blockEvent.BlockHash);
                }
            }
            return result.ToArray();
        }

        private FilterLog[] GetLogs(int filterId, bool advance)
        {
            if (_filterStore.GetFilter<LogFilter>(filterId) is not { } filter)
                return [];

            BlockEvent[]? events = GetEvents(filterId, advance);
            if (events is null)
                return [];

            using ArrayPoolListRef<FilterLog> result = new(0);
            foreach (BlockEvent blockEvent in events)
            {
                if (blockEvent.Bloom is not null && !filter.Matches(blockEvent.Bloom))
                {
                    continue;
                }

                long logIndex = 0;
                foreach (TxReceipt receipt in blockEvent.Receipts)
                {
                    LogEntry[]? entries = receipt.Logs;
                    if (entries is null)
                    {
                        continue;
                    }

                    for (int i = 0; i < entries.Length; i++)
                    {
                        FilterLog? filterLog = CreateLog(filter, receipt, entries[i], logIndex++, blockEvent.Timestamp, blockEvent.Removed);
                        if (filterLog is not null)
                        {
                            result.Add(filterLog);
                        }
                    }
                }
            }
            return result.ToArray();
        }

        private static FilterLog? CreateLog(LogFilter logFilter, TxReceipt txReceipt, LogEntry logEntry, long index, ulong blockTimestamp, bool removed = false)
        {
            if (logFilter.FromBlock.Type == BlockParameterType.BlockNumber &&
                logFilter.FromBlock.BlockNumber > txReceipt.BlockNumber)
            {
                return null;
            }

            if (logFilter.ToBlock.Type == BlockParameterType.BlockNumber && logFilter.ToBlock.BlockNumber < txReceipt.BlockNumber)
            {
                return null;
            }

            if (!logFilter.Accepts(logEntry))
            {
                return null;
            }

            if (logFilter.FromBlock.Type == BlockParameterType.Earliest
                || logFilter.FromBlock.Type == BlockParameterType.Pending
                || logFilter.ToBlock.Type == BlockParameterType.Earliest
                || logFilter.ToBlock.Type == BlockParameterType.Pending)
            {
                return new FilterLog(index, txReceipt, logEntry, blockTimestamp, removed);
            }

            if (logFilter.FromBlock.Type == BlockParameterType.Latest || logFilter.ToBlock.Type == BlockParameterType.Latest)
            {
                //TODO: check if is last mined block
                return new FilterLog(index, txReceipt, logEntry, blockTimestamp, removed);
            }

            return new FilterLog(index, txReceipt, logEntry, blockTimestamp, removed);
        }

        private sealed record BlockEvent(Hash256 BlockHash, ulong Timestamp, Bloom? Bloom, TxReceipt[] Receipts, bool Removed);

        private sealed class Option<T>(T value)
        {
            private bool _isRemoved;

            public T Value { get; } = value;
            public bool IsRemoved => Volatile.Read(ref _isRemoved);

            public void MarkRemoved() => Volatile.Write(ref _isRemoved, true);
        }
    }
}
