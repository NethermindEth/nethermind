//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Filters
{
    public class FilterManager : IFilterManager
    {
        private readonly ConcurrentDictionary<int, List<FilterLog>> _logs =
            new();

        private readonly ConcurrentDictionary<int, List<Keccak>> _blockHashes =
            new();

        private readonly ConcurrentDictionary<int, List<Keccak>> _pendingTransactions =
            new();

        private Keccak _lastBlockHash;
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
            if (_blockHashes.TryRemove(e.FilterId, out _))
            {
                return;
            }

            _logs.TryRemove(e.FilterId, out _);
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
            var filters = _filterStore.GetFilters<PendingTransactionFilter>();
            if (filters == null || filters.Length == 0)
            {
                return;
            }

            for (var i = 0; i < filters.Length; i++)
            {
                var filterId = filters[i].Id;
                var transactions = _pendingTransactions.GetOrAdd(filterId, _ => new List<Keccak>());
                transactions.Add(e.Transaction.Hash);
                if (_logger.IsDebug) _logger.Debug($"Filter with id: '{filterId}' contains {transactions.Count} transactions.");
            }
        }

        private void OnRemovedPendingTransaction(object sender, TxPool.TxEventArgs e)
        {
            var filters = _filterStore.GetFilters<PendingTransactionFilter>();
            if (filters == null || filters.Length == 0)
            {
                return;
            }

            for (var i = 0; i < filters.Length; i++)
            {
                var filterId = filters[i].Id;
                var transactions = _pendingTransactions.GetOrAdd(filterId, _ => new List<Keccak>());
                transactions.Remove(e.Transaction.Hash);
                if (_logger.IsDebug) _logger.Debug($"Filter with id: '{filterId}' contains {transactions.Count} transactions.");
            }
        }

        public FilterLog[] GetLogs(int filterId)
        {
            _logs.TryGetValue(filterId, out List<FilterLog> logs);
            return logs?.ToArray() ?? Array.Empty<FilterLog>();
        }

        public Keccak[] GetBlocksHashes(int filterId)
        {
            _blockHashes.TryGetValue(filterId, out List<Keccak> blockHashes);
            return blockHashes?.ToArray() ?? Array.Empty<Keccak>();
        }

        [Todo("Truffle sends transaction first and then polls so we hack it here for now")]
        public Keccak[] PollBlockHashes(int filterId)
        {
            if (!_blockHashes.TryGetValue(filterId, out var blockHashes))
            {
                if (_lastBlockHash != null)
                {
                    Keccak[] hackedResult = {_lastBlockHash}; // truffle hack
                    _lastBlockHash = null;
                    return hackedResult;
                }

                return Array.Empty<Keccak>();
            }

            var existingBlockHashes = blockHashes.ToArray();
            _blockHashes[filterId].Clear();

            return existingBlockHashes;
        }

        public FilterLog[] PollLogs(int filterId)
        {
            if (!_logs.TryGetValue(filterId, out var logs))
            {
                return Array.Empty<FilterLog>();
            }

            var existingLogs = logs.ToArray();
            _logs[filterId].Clear();

            return existingLogs;
        }

        public Keccak[] PollPendingTransactionHashes(int filterId)
        {
            if (!_pendingTransactions.TryGetValue(filterId, out var pendingTransactions))
            {
                return Array.Empty<Keccak>();
            }

            var existingPendingTransactions = pendingTransactions.ToArray();
            _pendingTransactions[filterId].Clear();

            return existingPendingTransactions;
        }

        private void AddReceipts(params TxReceipt[] txReceipts)
        {
            if (txReceipts == null)
            {
                throw new ArgumentNullException(nameof(txReceipts));
            }

            if (txReceipts.Length == 0)
            {
                return;
            }

            var filters = _filterStore.GetFilters<LogFilter>();
            if (filters == null || filters.Length == 0)
            {
                return;
            }

            for (var i = 0; i < txReceipts.Length; i++)
            {
                StoreLogs(filters, txReceipts[i], ref _logIndex);
            }
        }

        private void AddBlock(Block block)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            var filters = _filterStore.GetFilters<BlockFilter>();
            if (filters == null || filters.Length == 0)
            {
                return;
            }

            StoreBlock(filters, block);
        }

        private void StoreBlock(BlockFilter[] filters, Block block)
        {
            for (var i = 0; i < filters.Length; i++)
            {
                StoreBlock(filters[i], block);
            }
        }

        private void StoreBlock(BlockFilter filter, Block block)
        {
            if (block.Hash == null)
            {
                throw new InvalidOperationException("Cannot filter on blocks without calculated hashes");
            }

            var blocks = _blockHashes.GetOrAdd(filter.Id, i => new List<Keccak>());
            blocks.Add(block.Hash);
            if (_logger.IsDebug) _logger.Debug($"Filter with id: '{filter.Id}' contains {blocks.Count} blocks.");
        }

        private void StoreLogs(LogFilter[] filters, TxReceipt txReceipt, ref long logIndex)
        {
            for (var i = 0; i < filters.Length; i++)
            {
                StoreLogs(filters[i], txReceipt, ref logIndex);
            }
        }

        private void StoreLogs(LogFilter filter, TxReceipt txReceipt, ref long logIndex)
        {
            if (txReceipt.Logs == null || txReceipt.Logs.Length == 0)
            {
                return;
            }

            var logs = _logs.GetOrAdd(filter.Id, i => new List<FilterLog>());
            for (var i = 0; i < txReceipt.Logs.Length; i++)
            {
                var logEntry = txReceipt.Logs[i];
                var filterLog = CreateLog(filter, txReceipt, logEntry, logIndex++, i);
                if (!(filterLog is null))
                {
                    logs.Add(filterLog);
                }
            }

            if (logs.Count == 0)
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug($"Filter with id: '{filter.Id}' contains {logs.Count} logs.");
        }

        private FilterLog CreateLog(LogFilter logFilter, TxReceipt txReceipt, LogEntry logEntry, long index, int transactionLogIndex)
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
                return new FilterLog(index, transactionLogIndex, txReceipt, logEntry);
            }

            if (logFilter.FromBlock.Type == BlockParameterType.Latest || logFilter.ToBlock.Type == BlockParameterType.Latest)
            {
                //TODO: check if is last mined block
                return new FilterLog(index, transactionLogIndex, txReceipt, logEntry);
            }

            return new FilterLog(index, transactionLogIndex, txReceipt, logEntry);
        }
    }
}
