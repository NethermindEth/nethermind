// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Filters;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Filters
{
    public class FilterManager : IFilterManager
    {
        private readonly ConcurrentDictionary<int, List<FilterLog>> _logs =
            new();

        private readonly ConcurrentDictionary<int, List<Hash256>> _blockHashes =
            new();

        private readonly ConcurrentDictionary<int, List<Hash256>> _pendingTransactions =
            new();

        private Hash256? _lastBlockHash;
        private readonly IFilterStore _filterStore;
        private readonly ILogger _logger;
        private long _logIndex;

        public FilterManager(
            IFilterStore filterStore,
            IBlockProcessor blockProcessor,
            ITxPool txPool,
            ILogManager logManager)
        {
            _filterStore = filterStore ?? throw new ArgumentNullException(nameof(filterStore));
            blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            blockProcessor.BlockProcessed += OnBlockProcessed;
            blockProcessor.TransactionProcessed += OnTransactionProcessed;
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
            AddReceipts(e.TxReceipt);
        }

        private void OnNewPendingTransaction(object sender, TxPool.TxEventArgs e)
        {
            IEnumerable<PendingTransactionFilter> filters = _filterStore.GetFilters<PendingTransactionFilter>();
            foreach (PendingTransactionFilter filter in filters)
            {
                int filterId = filter.Id;
                List<Hash256> transactions = _pendingTransactions.GetOrAdd(filterId, static _ => new List<Hash256>());
                transactions.Add(e.Transaction.Hash);
                if (_logger.IsDebug) _logger.Debug($"Filter with id: {filterId} contains {transactions.Count} transactions.");

            }
        }

        private void OnRemovedPendingTransaction(object sender, TxPool.TxEventArgs e)
        {
            IEnumerable<PendingTransactionFilter> filters = _filterStore.GetFilters<PendingTransactionFilter>();

            foreach (PendingTransactionFilter filter in filters)
            {
                int filterId = filter.Id;
                List<Hash256> transactions = _pendingTransactions.GetOrAdd(filterId, static _ => new List<Hash256>());
                transactions.Remove(e.Transaction.Hash);
                if (_logger.IsDebug) _logger.Debug($"Filter with id: {filterId} contains {transactions.Count} transactions.");

            }
        }

        public FilterLog[] GetLogs(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            _logs.TryGetValue(filterId, out List<FilterLog> logs);
            return logs?.ToArray() ?? [];
        }

        public Hash256[] GetBlocksHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            _blockHashes.TryGetValue(filterId, out List<Hash256> blockHashes);
            return blockHashes?.ToArray() ?? [];
        }

        [Todo("Truffle sends transaction first and then polls so we hack it here for now")]
        public Hash256[] PollBlockHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            if (!_blockHashes.TryGetValue(filterId, out var blockHashes))
            {
                if (_lastBlockHash is not null)
                {
                    Hash256[] hackedResult = { _lastBlockHash }; // truffle hack
                    _lastBlockHash = null;
                    return hackedResult;
                }

                return [];
            }

            var existingBlockHashes = blockHashes.ToArray();
            _blockHashes[filterId].Clear();

            return existingBlockHashes;
        }

        public FilterLog[] PollLogs(int filterId)
        {
            _filterStore.RefreshFilter(filterId);
            if (!_logs.TryGetValue(filterId, out var logs))
            {
                return [];
            }

            var existingLogs = logs.ToArray();
            _logs[filterId].Clear();

            return existingLogs;
        }

        public Hash256[] PollPendingTransactionHashes(int filterId)
        {
            _filterStore.RefreshFilter(filterId);

            if (!_pendingTransactions.TryGetValue(filterId, out var pendingTransactions))
            {
                return [];
            }

            var existingPendingTransactions = pendingTransactions.ToArray();
            _pendingTransactions[filterId].Clear();

            return existingPendingTransactions;
        }

        private void AddReceipts(TxReceipt txReceipt)
        {
            ArgumentNullException.ThrowIfNull(txReceipt);

            IEnumerable<LogFilter> filters = _filterStore.GetFilters<LogFilter>();
            foreach (LogFilter filter in filters)
            {
                StoreLogs(filter, txReceipt, _logIndex);
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
            blocks.Add(block.Hash);
            if (_logger.IsDebug) _logger.Debug($"Filter with id: {filter.Id} contains {blocks.Count} blocks.");
        }

        private void StoreLogs(LogFilter filter, TxReceipt txReceipt, long logIndex)
        {
            if (txReceipt.Logs is null || txReceipt.Logs.Length == 0)
            {
                return;
            }

            List<FilterLog> logs = _logs.GetOrAdd(filter.Id, static i => new List<FilterLog>());
            for (int i = 0; i < txReceipt.Logs.Length; i++)
            {
                LogEntry? logEntry = txReceipt.Logs[i];
                FilterLog? filterLog = CreateLog(filter, txReceipt, logEntry, logIndex++);
                if (filterLog is not null)
                {
                    logs.Add(filterLog);
                }
            }

            if (logs.Count == 0)
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug($"Filter with id: {filter.Id} contains {logs.Count} logs.");
        }

        private static FilterLog? CreateLog(LogFilter logFilter, TxReceipt txReceipt, LogEntry logEntry, long index)
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
                return new FilterLog(index, txReceipt, logEntry);
            }

            if (logFilter.FromBlock.Type == BlockParameterType.Latest || logFilter.ToBlock.Type == BlockParameterType.Latest)
            {
                //TODO: check if is last mined block
                return new FilterLog(index, txReceipt, logEntry);
            }

            return new FilterLog(index, txReceipt, logEntry);
        }
    }
}
