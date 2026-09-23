// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    /// Filters do not buffer results. Processed blocks with their receipts and reorg removals (for log filters),
    /// block hashes (for block filters) and new pending transactions are each appended once to a log shared by all
    /// filters of that kind, every filter holds only a cursor into it, and its results are matched when it is polled.
    /// Memory is therefore independent of the number of filters and of how many results they match: a log keeps what
    /// the least recently polled filter has not read yet, and a filter that stops polling is removed by the
    /// <see cref="FilterStore"/> timeout. Log filters keep only the logs of each block, not its receipts, and an idle
    /// one keeps the logs of every block processed within that timeout.
    /// </remarks>
    public sealed class FilterManager
    {
        private readonly FilterEventLog<BlockEvent> _blocks;
        private readonly FilterEventLog<Hash256> _blockHashes;
        private readonly FilterEventLog<PendingTransaction> _pendingTransactions;

        private Hash256? _lastBlockHash;
        private readonly FilterStore _filterStore;
        private readonly ITxPool _txPool;

        public FilterManager(
            FilterStore filterStore,
            IMainProcessingContext mainProcessingContext,
            ITxPool txPool,
            IReceiptMonitor receiptMonitor,
            ILogManager logManager)
        {
            _filterStore = filterStore ?? throw new ArgumentNullException(nameof(filterStore));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            ArgumentNullException.ThrowIfNull(receiptMonitor);
            ArgumentNullException.ThrowIfNull(logManager);
            _blocks = new FilterEventLog<BlockEvent>(filterStore);
            _blockHashes = new FilterEventLog<Hash256>(filterStore);
            _pendingTransactions = new FilterEventLog<PendingTransaction>(filterStore);
            mainProcessingContext.BranchProcessor.BlockProcessed += OnBlockProcessed;
            receiptMonitor.ReceiptsInserted += OnReceiptsInserted;
            _filterStore.FilterSaved += OnFilterSaved;
            _filterStore.FilterRemoved += OnFilterRemoved;
            txPool.NewPending += OnNewPendingTransaction;

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
                case LogFilter:
                    _blocks.Track(filter.Id);
                    break;
                case BlockFilter:
                    _blockHashes.Track(filter.Id);
                    break;
                case PendingTransactionFilter:
                    _pendingTransactions.Track(filter.Id);
                    break;
            }
        }

        private void OnFilterRemoved(object sender, FilterEventArgs e)
        {
            _blocks.Untrack(e.FilterId);
            _blockHashes.Untrack(e.FilterId);
            _pendingTransactions.Untrack(e.FilterId);
        }

        private void OnBlockProcessed(object sender, BlockProcessedEventArgs e)
        {
            Block block = e.Block;
            Hash256 blockHash = block.Hash ?? throw new InvalidOperationException("Cannot filter on blocks without calculated hashes");
            _lastBlockHash = blockHash;
            _blockHashes.Append(blockHash);
            if (_blocks.IsTracking)
            {
                AppendLogs(block.Timestamp, block.Header.Bloom, e.TxReceipts, removed: false);
            }
        }

        private void OnReceiptsInserted(object? sender, ReceiptsEventArgs e)
        {
            if (!e.WasRemoved || e.TxReceipts is null || e.TxReceipts.Length == 0 || !_blocks.IsTracking)
            {
                return;
            }

            AppendLogs(e.BlockHeader.Timestamp, e.BlockHeader.Bloom, e.TxReceipts, removed: true);
        }

        /// <summary>
        /// Appends the logs of a block for log filters, keeping per transaction only what a <see cref="FilterLog"/> needs.
        /// </summary>
        private void AppendLogs(ulong timestamp, Bloom? bloom, TxReceipt[] receipts, bool removed)
        {
            int count = 0;
            foreach (TxReceipt receipt in receipts)
            {
                if (receipt.Logs?.Length > 0) count++;
            }

            if (count == 0)
            {
                return;
            }

            TransactionLogs[] transactions = new TransactionLogs[count];
            int index = 0;
            foreach (TxReceipt receipt in receipts)
            {
                if (receipt.Logs is { Length: > 0 } logs)
                {
                    transactions[index++] = new TransactionLogs(receipt.BlockNumber, receipt.BlockHash!, receipt.Index, receipt.TxHash!, logs);
                }
            }

            _blocks.Append(new BlockEvent(timestamp, bloom, transactions, removed));
        }

        private void OnNewPendingTransaction(object sender, TxPool.TxEventArgs e)
        {
            if (e.Transaction.Hash is { } hash && _pendingTransactions.IsTracking)
            {
                _pendingTransactions.Append(new PendingTransaction(hash, e.Transaction.Type));
            }
        }

        public FilterLog[] GetLogs(int filterId) => GetLogs(filterId, advance: false);

        public Hash256[] GetBlocksHashes(int filterId) =>
            _blockHashes.Read(filterId, advance: false, out _) is { } blockHashes ? ToArray(blockHashes) : [];

        [Todo("Truffle sends transaction first and then polls so we hack it here for now")]
        public Hash256[] PollBlockHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            if (_blockHashes.Read(filterId, advance: true, out bool noEventSinceTracked) is not { } blockHashes || noEventSinceTracked)
            {
                if (_lastBlockHash is not null)
                {
                    Hash256[] hackedResult = { _lastBlockHash }; // truffle hack
                    _lastBlockHash = null;
                    return hackedResult;
                }

                return [];
            }

            return ToArray(blockHashes);
        }

        public FilterLog[] PollLogs(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            return GetLogs(filterId, advance: true);
        }

        /// <remarks>
        /// Reports only the transactions that are still in the pool, so ones included, replaced or evicted since they
        /// arrived are skipped.
        /// </remarks>
        public Hash256[] PollPendingTransactionHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            if (_pendingTransactions.Read(filterId, advance: true, out _) is not { } transactions)
                return [];

            using ArrayPoolListRef<Hash256> result = new(transactions.Count);
            foreach (PendingTransaction transaction in transactions)
            {
                if (_txPool.ContainsTx(transaction.Hash, transaction.Type))
                {
                    result.Add(transaction.Hash);
                }
            }
            return result.ToArray();
        }

        private static Hash256[] ToArray(FilterEventLog<Hash256>.Events blockHashes)
        {
            using ArrayPoolListRef<Hash256> result = new(blockHashes.Count);
            foreach (Hash256 blockHash in blockHashes)
            {
                result.Add(blockHash);
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
                foreach (TransactionLogs transaction in blockEvent.Transactions)
                {
                    LogEntry[] entries = transaction.Logs;
                    for (int i = 0; i < entries.Length; i++)
                    {
                        FilterLog? filterLog = CreateLog(filter, transaction, entries[i], logIndex++, blockEvent.Timestamp, blockEvent.Removed);
                        if (filterLog is not null)
                        {
                            result.Add(filterLog);
                        }
                    }
                }
            }
            return result.ToArray();
        }

        private static FilterLog? CreateLog(LogFilter logFilter, in TransactionLogs transaction, LogEntry logEntry, long index, ulong blockTimestamp, bool removed = false)
        {
            if (logFilter.FromBlock.Type == BlockParameterType.BlockNumber &&
                logFilter.FromBlock.BlockNumber > transaction.BlockNumber)
            {
                return null;
            }

            if (logFilter.ToBlock.Type == BlockParameterType.BlockNumber && logFilter.ToBlock.BlockNumber < transaction.BlockNumber)
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
                return NewLog(transaction, logEntry, index, blockTimestamp, removed);
            }

            if (logFilter.FromBlock.Type == BlockParameterType.Latest || logFilter.ToBlock.Type == BlockParameterType.Latest)
            {
                //TODO: check if is last mined block
                return NewLog(transaction, logEntry, index, blockTimestamp, removed);
            }

            return NewLog(transaction, logEntry, index, blockTimestamp, removed);
        }

        private static FilterLog NewLog(in TransactionLogs transaction, LogEntry logEntry, long index, ulong blockTimestamp, bool removed) =>
            new(index, transaction.BlockNumber, blockTimestamp, transaction.BlockHash, transaction.Index, transaction.TxHash,
                logEntry.Address, logEntry.Data, logEntry.Topics, removed);

        private sealed record BlockEvent(ulong Timestamp, Bloom? Bloom, TransactionLogs[] Transactions, bool Removed);

        private readonly record struct TransactionLogs(ulong BlockNumber, Hash256 BlockHash, int Index, Hash256 TxHash, LogEntry[] Logs);

        private readonly record struct PendingTransaction(Hash256 Hash, TxType Type);

        /// <summary>
        /// Events shared by the filters of one kind, each filter reading them through its own cursor.
        /// </summary>
        /// <remarks>
        /// The events form an append-only linked list whose nodes never change once linked, so a read takes two
        /// references under the lock and walks the list outside it without copying. A cursor points at the link after
        /// the last event its filter read, so an event stays reachable only until every tracked filter has read it,
        /// and the garbage collector releases the rest. Only reads that advance a cursor keep a filter alive in
        /// <see cref="FilterStore"/>, so a filter that stops polling times out and stops holding events back.
        /// </remarks>
        private sealed class FilterEventLog<TEvent>(FilterStore filterStore)
        {
            private readonly Lock _lock = new();
            private readonly Dictionary<int, Cursor> _cursors = [];
            private Link _tail = new(0);
            private bool _isTracking;

            /// <summary>
            /// Whether any filter is tracked, read without locking so callers can skip building an event nobody reads.
            /// </summary>
            /// <remarks>
            /// A filter tracked concurrently may miss that event, as if it had been installed a moment later.
            /// </remarks>
            public bool IsTracking => Volatile.Read(ref _isTracking);

            public void Track(int filterId)
            {
                lock (_lock)
                {
                    if (!_cursors.TryAdd(filterId, new Cursor(_tail)))
                    {
                        return;
                    }

                    Volatile.Write(ref _isTracking, true);
                }

                // A removal that ran before tracking would otherwise leave a cursor holding every later event.
                if (!filterStore.FilterExists(filterId))
                {
                    Untrack(filterId);
                }
            }

            public void Untrack(int filterId)
            {
                lock (_lock)
                {
                    if (_cursors.Remove(filterId) && _cursors.Count == 0)
                    {
                        Volatile.Write(ref _isTracking, false);
                    }
                }
            }

            /// <summary>
            /// Appends an event for the tracked filters to read; does nothing while no filter is tracked.
            /// </summary>
            public void Append(TEvent item)
            {
                if (!IsTracking)
                {
                    return;
                }

                lock (_lock)
                {
                    if (_cursors.Count == 0)
                    {
                        return;
                    }

                    Node node = new(item, _tail.Sequence + 1);
                    _tail.Next = node;
                    _tail = node.After;
                }
            }

            /// <summary>
            /// Returns the events the filter has not read yet, or <c>null</c> when the filter is not tracked.
            /// </summary>
            /// <param name="filterId">The filter to read for.</param>
            /// <param name="advance">Whether to mark the returned events as read.</param>
            /// <param name="noEventSinceTracked">Whether no event was appended since the filter was tracked.</param>
            public Events? Read(int filterId, bool advance, out bool noEventSinceTracked)
            {
                lock (_lock)
                {
                    if (!_cursors.TryGetValue(filterId, out Cursor? cursor))
                    {
                        noEventSinceTracked = false;
                        return null;
                    }

                    Link end = _tail;
                    noEventSinceTracked = end.Sequence == cursor.TrackedSequence;
                    Link start = cursor.Position;
                    if (advance)
                    {
                        cursor.Position = end;
                    }

                    return new Events(start, end);
                }
            }

            /// <summary>
            /// A range of events, enumerable without the lock because linked nodes never change.
            /// </summary>
            public readonly struct Events(Link start, Link end)
            {
                public int Count => (int)(end.Sequence - start.Sequence);

                public Enumerator GetEnumerator() => new(start, end);

                public struct Enumerator(Link start, Link end)
                {
                    private Link _position = start;
                    private TEvent _current = default!;

                    public readonly TEvent Current => _current;

                    public bool MoveNext()
                    {
                        if (_position == end)
                        {
                            return false;
                        }

                        Node node = _position.Next!;
                        _current = node.Value;
                        _position = node.After;
                        return true;
                    }
                }
            }

            /// <summary>
            /// The point after an event, or the start of the log; <see cref="Sequence"/> counts the events before it.
            /// </summary>
            public sealed class Link(long sequence)
            {
                public long Sequence { get; } = sequence;
                public Node? Next;
            }

            public sealed class Node(TEvent value, long sequence)
            {
                public TEvent Value { get; } = value;
                public Link After { get; } = new(sequence);
            }

            private sealed class Cursor(Link position)
            {
                public Link Position = position;
                public long TrackedSequence { get; } = position.Sequence;
            }
        }
    }
}
