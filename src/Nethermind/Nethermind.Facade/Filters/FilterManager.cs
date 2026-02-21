// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private readonly ConcurrentDictionary<int, List<FilterLog>> _logs =
            new();

        private readonly ConcurrentDictionary<int, List<Hash256>> _blockHashes =
            new();

        private readonly ConcurrentDictionary<int, List<Hash256>> _pendingTransactions =
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
                List<Hash256> transactions = _pendingTransactions.GetOrAdd(filterId, static _ => new List<Hash256>());
                lock (transactions)
                {
                    transactions.Add(e.Transaction.Hash);
                    if (_logger.IsTrace) _logger.Trace($"Filter with id: {filterId} contains {transactions.Count} transactions.");
                }
            }
        }

        private void OnRemovedPendingTransaction(object sender, TxPool.TxEventArgs e)
        {
            IEnumerable<PendingTransactionFilter> filters = _filterStore.GetFilters<PendingTransactionFilter>();

            foreach (PendingTransactionFilter filter in filters)
            {
                int filterId = filter.Id;
                List<Hash256> transactions = _pendingTransactions.GetOrAdd(filterId, static _ => new List<Hash256>());
                lock (transactions)
                {
                    transactions.Remove(e.Transaction.Hash);
                    if (_logger.IsTrace) _logger.Trace($"Filter with id: {filterId} contains {transactions.Count} transactions.");
                }
            }
        }

        public FilterLog[] GetLogs(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            if (_logs.TryGetValue(filterId, out List<FilterLog> logs))
            {
                lock (logs)
                {
                    if (_logs.TryGetValue(filterId, out List<FilterLog> current) && ReferenceEquals(current, logs))
                        return logs.ToArray();
                }
            }
            return [];
        }

        public Hash256[] GetBlocksHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            if (_blockHashes.TryGetValue(filterId, out List<Hash256> blockHashes))
            {
                lock (blockHashes)
                {
                    if (_blockHashes.TryGetValue(filterId, out List<Hash256> current) && ReferenceEquals(current, blockHashes))
                        return blockHashes.ToArray();
                }
            }
            return [];
        }

        [Todo("Truffle sends transaction first and then polls so we hack it here for now")]
        public Hash256[] PollBlockHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            if (!_blockHashes.TryGetValue(filterId, out List<Hash256> blockHashes))
            {
                if (_lastBlockHash is not null)
                {
                    Hash256[] hackedResult = { _lastBlockHash }; // truffle hack
                    _lastBlockHash = null;
                    return hackedResult;
                }

                return [];
            }

            lock (blockHashes)
            {
                if (!_blockHashes.TryGetValue(filterId, out List<Hash256> current) || !ReferenceEquals(current, blockHashes))
                    return [];

                Hash256[] existingBlockHashes = blockHashes.ToArray();
                blockHashes.Clear();
                return existingBlockHashes;
            }
        }

        public FilterLog[] PollLogs(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            if (!_logs.TryGetValue(filterId, out List<FilterLog> logs))
            {
                return [];
            }

            lock (logs)
            {
                if (!_logs.TryGetValue(filterId, out List<FilterLog> current) || !ReferenceEquals(current, logs))
                    return [];

                FilterLog[] existingLogs = logs.ToArray();
                logs.Clear();
                return existingLogs;
            }
        }

        public Hash256[] PollPendingTransactionHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);

            if (!_pendingTransactions.TryGetValue(filterId, out List<Hash256> pendingTransactions))
            {
                return [];
            }

            lock (pendingTransactions)
            {
                if (!_pendingTransactions.TryGetValue(filterId, out List<Hash256> current) || !ReferenceEquals(current, pendingTransactions))
                    return [];

                Hash256[] existingPendingTransactions = pendingTransactions.ToArray();
                pendingTransactions.Clear();
                return existingPendingTransactions;
            }
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

            List<Hash256> blocks = _blockHashes.GetOrAdd(filter.Id, static i => new List<Hash256>());
            lock (blocks)
            {
                blocks.Add(block.Hash);
                if (_logger.IsTrace) _logger.Trace($"Filter with id: {filter.Id} contains {blocks.Count} blocks.");
            }
        }

        private void StoreLogs(LogFilter filter, TxReceipt txReceipt, long logIndex, ulong blockTimestamp)
        {
            if (txReceipt.Logs is null || txReceipt.Logs.Length == 0)
            {
                return;
            }

            // Fetch (or create) the list before processing logs so that a concur rent OnFilterRemoved
            // cannot cause GetOrAdd to resurrect a removed entry in _logs after TryRemove has run.
            List<FilterLog> logs = _logs.GetOrAdd(filter.Id, static i => new List<FilterLog>());
            using ArrayPoolListRef<FilterLog> filteredLogs = new(txReceipt.Logs.Length);
            for (int i = 0; i < txReceipt.Logs.Length; i++)
            {
                LogEntry? logEntry = txReceipt.Logs[i];
                FilterLog? filterLog = CreateLog(filter, txReceipt, logEntry, logIndex++, blockTimestamp);
                if (filterLog is not null)
                {
                    filteredLogs.Add(filterLog);
                }
            }

            if (filteredLogs.Count == 0)
            {
                return;
            }

            lock (logs)
            {
                logs.AddRange(filteredLogs.AsSpan());
                if (_logger.IsTrace) _logger.Trace($"Filter with id: {filter.Id} contains {logs.Count} logs.");
            }
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
    }
}
