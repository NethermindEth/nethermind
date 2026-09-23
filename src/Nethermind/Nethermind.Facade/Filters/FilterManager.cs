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
    /// Filters do not buffer results. Processed blocks (with their receipts), reorg removals and new pending
    /// transactions are each appended once to a log shared by all filters of that kind, every filter holds only a
    /// cursor into it, and its results are matched when it is polled. Memory is therefore independent of the number
    /// of filters and of how many results they match: a log keeps what the least recently polled filter has not read
    /// yet, and a filter that stops polling is removed by the <see cref="FilterStore"/> timeout.
    /// </remarks>
    public sealed class FilterManager
    {
        // Pending transactions arrive far more often than blocks, so their log is trimmed in batches.
        private const int PendingTransactionsTrimInterval = 256;

        private readonly FilterEventLog<BlockEvent> _blocks;
        private readonly FilterEventLog<Option<Hash256>> _pendingTransactions;
        private readonly ConcurrentDictionary<Hash256, Option<Hash256>> _pendingTransactionsByHash = new();

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
            _blocks = new FilterEventLog<BlockEvent>(filterStore);
            _pendingTransactions = new FilterEventLog<Option<Hash256>>(filterStore, OnPendingTransactionDropped);
            mainProcessingContext.BranchProcessor.BlockProcessed += OnBlockProcessed;
            receiptMonitor.ReceiptsInserted += OnReceiptsInserted;
            _filterStore.FilterSaved += OnFilterSaved;
            _filterStore.FilterRemoved += OnFilterRemoved;
            txPool.NewPending += OnNewPendingTransaction;
            txPool.RemovedPending += OnRemovedPendingTransaction;

            // After subscribing, so a filter saved meanwhile is tracked either way.
            foreach (FilterBase filter in _filterStore.GetFilters<FilterBase>())
            {
                Track(filter);
            }
        }

        private void OnFilterSaved(object? sender, FilterEventArgs e)
        {
            if (_filterStore.GetFilter<FilterBase>(e.FilterId) is { } filter)
            {
                Track(filter);
            }
        }

        private void Track(FilterBase filter)
        {
            switch (filter)
            {
                case LogFilter or BlockFilter:
                    _blocks.Track(filter.Id);
                    break;
                case PendingTransactionFilter:
                    _pendingTransactions.Track(filter.Id);
                    break;
            }
        }

        private void OnFilterRemoved(object sender, FilterEventArgs e)
        {
            _blocks.Untrack(e.FilterId);
            _pendingTransactions.Untrack(e.FilterId);
        }

        private void OnBlockProcessed(object sender, BlockProcessedEventArgs e)
        {
            Block block = e.Block;
            Hash256 blockHash = block.Hash ?? throw new InvalidOperationException("Cannot filter on blocks without calculated hashes");
            _lastBlockHash = blockHash;
            _blocks.Append(new BlockEvent(blockHash, block.Timestamp, block.Header.Bloom, e.TxReceipts, Removed: false));
        }

        private void OnReceiptsInserted(object? sender, ReceiptsEventArgs e)
        {
            if (!e.WasRemoved || e.TxReceipts is null || e.TxReceipts.Length == 0)
            {
                return;
            }

            // The stored receipts are not guaranteed to carry blooms, so a removal is never skipped by bloom.
            _blocks.Append(new BlockEvent(e.BlockHeader.Hash!, e.BlockHeader.Timestamp, null, e.TxReceipts, Removed: true));
        }

        private void OnNewPendingTransaction(object sender, TxPool.TxEventArgs e)
        {
            if (e.Transaction.Hash is not { } hash)
            {
                return;
            }

            // Indexed before appending, so a trim that drops the entry right away also finds it in the index.
            Option<Hash256> transaction = new(hash);
            _pendingTransactionsByHash[hash] = transaction;
            if (!_pendingTransactions.Append(transaction, PendingTransactionsTrimInterval))
            {
                OnPendingTransactionDropped(transaction);
            }
        }

        private void OnPendingTransactionDropped(Option<Hash256> transaction) =>
            _pendingTransactionsByHash.TryRemove(new KeyValuePair<Hash256, Option<Hash256>>(transaction.Value, transaction));

        private void OnRemovedPendingTransaction(object sender, TxPool.TxEventArgs e)
        {
            if (e.Transaction.Hash is { } hash && _pendingTransactionsByHash.TryGetValue(hash, out Option<Hash256>? transaction))
            {
                transaction.MarkRemoved();
                if (_logger.IsTrace) _logger.Trace($"Pending transaction {hash} marked as removed.");
            }
        }

        public FilterLog[] GetLogs(int filterId) => GetLogs(filterId, advance: false);

        public Hash256[] GetBlocksHashes(int filterId) =>
            _blocks.Read(filterId, advance: false, out _) is { } events ? GetBlockHashes(events) : [];

        [Todo("Truffle sends transaction first and then polls so we hack it here for now")]
        public Hash256[] PollBlockHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            BlockEvent[]? events = _blocks.Read(filterId, advance: true, out bool noEventSinceTracked);
            if (events is null || noEventSinceTracked)
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
            if (_pendingTransactions.Read(filterId, advance: true, out _) is not { } transactions)
                return [];

            using ArrayPoolListRef<Hash256> result = new(transactions.Length);
            foreach (Option<Hash256> transaction in transactions)
            {
                if (!transaction.IsRemoved)
                {
                    result.Add(transaction.Value);
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
            if (_filterStore.GetFilter<LogFilter>(filterId) is not { } filter
                || _blocks.Read(filterId, advance, out _) is not { } events)
            {
                return [];
            }

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

        /// <summary>
        /// Events shared by the filters of one kind, each filter reading them through its own cursor.
        /// </summary>
        /// <remarks>
        /// An event is kept until every tracked filter has read it, and nothing is kept while no filter is tracked.
        /// Only reads that advance a cursor keep a filter alive in <see cref="FilterStore"/>, so a filter that stops
        /// polling times out and stops holding events back.
        /// </remarks>
        private sealed class FilterEventLog<TEvent>(FilterStore filterStore, Action<TEvent>? dropped = null)
        {
            private readonly Lock _lock = new();
            private readonly List<TEvent> _events = [];
            private readonly Dictionary<int, Cursor> _cursors = [];
            private long _firstSequence;
            private int _appendsSinceTrim;

            private long NextSequence => _firstSequence + _events.Count;

            public void Track(int filterId)
            {
                lock (_lock)
                {
                    long next = NextSequence;
                    _cursors.TryAdd(filterId, new Cursor(next, next));
                }
            }

            public void Untrack(int filterId)
            {
                lock (_lock)
                {
                    if (_cursors.Remove(filterId) && _cursors.Count == 0)
                    {
                        Drop(_events.Count);
                    }
                }
            }

            /// <summary>
            /// Appends an event for the tracked filters to read.
            /// </summary>
            /// <returns><c>false</c> when no filter is tracked, so the event was not kept.</returns>
            public bool Append(TEvent item, int trimInterval = 1)
            {
                lock (_lock)
                {
                    if (_cursors.Count == 0)
                    {
                        return false;
                    }

                    _events.Add(item);
                    if (++_appendsSinceTrim >= trimInterval)
                    {
                        _appendsSinceTrim = 0;
                        Trim();
                    }

                    return true;
                }
            }

            /// <summary>
            /// Returns the events the filter has not read yet, or <c>null</c> when the filter is not tracked.
            /// </summary>
            /// <param name="filterId">The filter to read for.</param>
            /// <param name="advance">Whether to mark the returned events as read.</param>
            /// <param name="noEventSinceTracked">Whether no event was appended since the filter was tracked.</param>
            public TEvent[]? Read(int filterId, bool advance, out bool noEventSinceTracked)
            {
                lock (_lock)
                {
                    if (!_cursors.TryGetValue(filterId, out Cursor cursor))
                    {
                        noEventSinceTracked = false;
                        return null;
                    }

                    long next = NextSequence;
                    noEventSinceTracked = next == cursor.Tracked;
                    if (advance)
                    {
                        _cursors[filterId] = cursor with { Next = next };
                    }

                    return CollectionsMarshal.AsSpan(_events)[(int)(cursor.Next - _firstSequence)..].ToArray();
                }
            }

            private void Trim()
            {
                long oldest = NextSequence;
                using ArrayPoolListRef<int> removed = new(0);
                foreach (KeyValuePair<int, Cursor> cursor in _cursors)
                {
                    // Covers a filter whose removal raced its tracking, which would otherwise hold events forever.
                    if (!filterStore.FilterExists(cursor.Key))
                    {
                        removed.Add(cursor.Key);
                    }
                    else
                    {
                        oldest = Math.Min(oldest, cursor.Value.Next);
                    }
                }

                foreach (int id in removed.AsSpan())
                {
                    _cursors.Remove(id);
                }

                Drop((int)(oldest - _firstSequence));
            }

            private void Drop(int count)
            {
                if (count <= 0)
                {
                    return;
                }

                if (dropped is not null)
                {
                    foreach (TEvent item in CollectionsMarshal.AsSpan(_events)[..count])
                    {
                        dropped(item);
                    }
                }

                _events.RemoveRange(0, count);
                _firstSequence += count;
            }

            private readonly record struct Cursor(long Next, long Tracked);
        }

        private sealed class Option<T>(T value)
        {
            private bool _isRemoved;

            public T Value { get; } = value;
            public bool IsRemoved => Volatile.Read(ref _isRemoved);

            public void MarkRemoved() => Volatile.Write(ref _isRemoved, true);
        }
    }
}
