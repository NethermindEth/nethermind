// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Filters;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Filters
{
    public sealed class FilterManager
    {
        private readonly ConcurrentDictionary<int, ConcurrentQueue<FilterLog>> _logs =
            new();

        private readonly ConcurrentDictionary<int, ConcurrentQueue<Hash256>> _blockHashes =
            new();

        private readonly ConcurrentDictionary<int, ConcurrentQueue<Option<Hash256>>> _pendingTransactions =
            new();

        private Hash256? _lastBlockHash;
        private readonly FilterStore _filterStore;
        private readonly ILogger _logger;
        private long _logIndex;

        public FilterManager(
            FilterStore filterStore,
            IMainProcessingContext mainProcessingContext,
            ITxPool txPool,
            ILogManager logManager)
        {
            _filterStore = filterStore ?? throw new ArgumentNullException(nameof(filterStore));
            txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            mainProcessingContext.BranchProcessor.BlockProcessed += OnBlockProcessed;
            mainProcessingContext.TransactionProcessed += OnTransactionProcessed;
            _filterStore.FilterRemoved += OnFilterRemoved;
            txPool.NewPending += OnNewPendingTransaction;
            txPool.RemovedPending += OnRemovedPendingTransaction;
        }

        private void OnFilterRemoved(object sender, FilterEventArgs e)
        {
            int id = e.FilterId;
            if (_blockHashes.TryRemove(id, out _)) return;
            if (_logs.TryRemove(id, out _)) return;
            _pendingTransactions.TryRemove(id, out _);
        }

        private void OnBlockProcessed(object sender, BlockProcessedEventArgs e)
        {
            _lastBlockHash = e.Block.Hash;
            _logIndex = 0;
            AddBlock(e.Block);
        }

        private void OnTransactionProcessed(object sender, TxProcessedEventArgs e)
        {
            AddReceipts(e.TxReceipt, e.BlockHeader.Timestamp);
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
            return _logs.TryGetValue(filterId, out ConcurrentQueue<FilterLog> logs) ? logs.ToArray() : [];
        }

        public Hash256[] GetBlocksHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            return _blockHashes.TryGetValue(filterId, out ConcurrentQueue<Hash256> blockHashes) ? blockHashes.ToArray() : [];
        }

        [Todo("Truffle sends transaction first and then polls so we hack it here for now")]
        public Hash256[] PollBlockHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            if (!_blockHashes.TryGetValue(filterId, out ConcurrentQueue<Hash256> blockHashes))
            {
                if (_lastBlockHash is not null)
                {
                    Hash256[] hackedResult = { _lastBlockHash }; // truffle hack
                    _lastBlockHash = null;
                    return hackedResult;
                }

                return [];
            }

            using ArrayPoolListRef<Hash256> result = new(blockHashes.Count);
            while (blockHashes.TryDequeue(out Hash256? hash))
            {
                result.Add(hash);
            }
            return result.ToArray();
        }

        public FilterLog[] PollLogs(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            if (!_logs.TryGetValue(filterId, out ConcurrentQueue<FilterLog> logs))
                return [];

            using ArrayPoolListRef<FilterLog> result = new(logs.Count);
            while (logs.TryDequeue(out FilterLog? log))
            {
                result.Add(log);
            }
            return result.ToArray();
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

        private void AddReceipts(TxReceipt txReceipt, ulong blockTimestamp)
        {
            ArgumentNullException.ThrowIfNull(txReceipt);

            IEnumerable<LogFilter> filters = _filterStore.GetFilters<LogFilter>();
            foreach (LogFilter filter in filters)
            {
                StoreLogs(filter, txReceipt, _logIndex, blockTimestamp);
            }

            _logIndex += txReceipt.Logs?.Length ?? 0;
        }

        private void AddBlock(Block block)
        {
            ArgumentNullException.ThrowIfNull(block);

            IEnumerable<BlockFilter> filters = _filterStore.GetFilters<BlockFilter>();

            foreach (BlockFilter filter in filters)
            {
                StoreBlock(filter, block);
            }
        }

        private void StoreBlock(BlockFilter filter, Block block)
        {
            if (block.Hash is null)
            {
                throw new InvalidOperationException("Cannot filter on blocks without calculated hashes");
            }

            ConcurrentQueue<Hash256> blocks = _blockHashes.GetOrAdd(filter.Id, static _ => new ConcurrentQueue<Hash256>());
            blocks.Enqueue(block.Hash);
            if (_logger.IsTrace) _logger.Trace($"Filter with id: {filter.Id} contains {blocks.Count} blocks.");
        }

        private void StoreLogs(LogFilter filter, TxReceipt txReceipt, long logIndex, ulong blockTimestamp)
        {
            if (txReceipt.Logs is null || txReceipt.Logs.Length == 0)
            {
                return;
            }

            ConcurrentQueue<FilterLog>? logs = null;
            for (int i = 0; i < txReceipt.Logs.Length; i++)
            {
                LogEntry? logEntry = txReceipt.Logs[i];
                FilterLog? filterLog = CreateLog(filter, txReceipt, logEntry, logIndex++, blockTimestamp);
                if (filterLog is not null)
                {
                    logs ??= _logs.GetOrAdd(filter.Id, static _ => new ConcurrentQueue<FilterLog>());
                    logs.Enqueue(filterLog);
                }
            }

            if (_logger.IsTrace && logs is not null)
                _logger.Trace($"Filter with id: {filter.Id} contains {logs.Count} logs.");
        }

        private static FilterLog? CreateLog(LogFilter logFilter, TxReceipt txReceipt, LogEntry logEntry, long index, ulong blockTimestamp)
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
                return new FilterLog(index, txReceipt, logEntry, blockTimestamp);
            }

            if (logFilter.FromBlock.Type == BlockParameterType.Latest || logFilter.ToBlock.Type == BlockParameterType.Latest)
            {
                //TODO: check if is last mined block
                return new FilterLog(index, txReceipt, logEntry, blockTimestamp);
            }

            return new FilterLog(index, txReceipt, logEntry, blockTimestamp);
        }

        private sealed class Option<T>
        {
            private bool _isRemoved;

            public T Value { get; }
            public bool IsRemoved => Volatile.Read(ref _isRemoved);

            public Option(T value)
            {
                Value = value;
            }

            public void MarkRemoved() => Volatile.Write(ref _isRemoved, true);
        }
    }
}
