/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Filters
{
    public class FilterManager : IFilterManager
    {
        private readonly ConcurrentDictionary<int, List<FilterLog>> _logs =
            new ConcurrentDictionary<int, List<FilterLog>>();

        private readonly ConcurrentDictionary<int, List<Keccak>> _blockHashes =
            new ConcurrentDictionary<int, List<Keccak>>();

        private readonly IFilterStore _filterStore;
        private readonly ILogger _logger;

        public FilterManager(IFilterStore filterStore, IBlockProcessor blockProcessor, ILogManager logManager)
        {
            _filterStore = filterStore ?? throw new ArgumentNullException(nameof(filterStore));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            blockProcessor.BlockProcessed += OnBlockProcessed;
            blockProcessor.TransactionProcessed += OnTransactionProcessed;

            _filterStore.FilterRemoved += FilterStoreOnFilterRemoved;
        }

        private void FilterStoreOnFilterRemoved(object sender, FilterEventArgs e)
        {
            if (_blockHashes.ContainsKey(e.FilterId))
            {
                _blockHashes.TryRemove(e.FilterId, out _);
            }
            else
            {
                _logs.TryRemove(e.FilterId, out _);
            }
        }

        private Keccak _lastBlockHash;

        private void OnBlockProcessed(object sender, BlockProcessedEventArgs e)
        {
            _lastBlockHash = e.Block.Hash;
            AddBlock(e.Block);
        }

        private void OnTransactionProcessed(object sender, TransactionProcessedEventArgs e)
        {
            AddTransactionReceipts(e.Receipt);
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
            if (!_blockHashes.ContainsKey(filterId))
            {
                if (_lastBlockHash != null)
                {
                    Keccak[] hackedResult = new[] {_lastBlockHash}; // truffle hack
                    _lastBlockHash = null;
                    return hackedResult;
                }

                return Array.Empty<Keccak>();
            }

            var blockHashes = _blockHashes[filterId].ToArray();
            _blockHashes[filterId].Clear();
            return blockHashes;
        }

        public FilterLog[] PollLogs(int filterId)
        {
            if (!_logs.ContainsKey(filterId))
            {
                return Array.Empty<FilterLog>();
            }

            var logs = _logs[filterId].ToArray();
            _logs[filterId].Clear();
            return logs;
        }

        private void AddTransactionReceipts(params TransactionReceipt[] receipts)
        {
            if (receipts == null)
            {
                throw new ArgumentNullException(nameof(receipts));
            }

            if (receipts.Length == 0)
            {
                return;
            }

            var filters = _filterStore.GetFilters<LogFilter>();
            if (filters == null || filters.Length == 0)
            {
                return;
            }

            for (var i = 0; i < receipts.Length; i++)
            {
                StoreLogs(filters, receipts[i]);
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
            _logger.Debug($"Filter with id: '{filter.Id}' contains {blocks.Count} blocks.");
        }

        private void StoreLogs(LogFilter[] filters, TransactionReceipt receipt)
        {
            for (var i = 0; i < filters.Length; i++)
            {
                StoreLogs(filters[i], receipt);
            }
        }

        private void StoreLogs(LogFilter filter, TransactionReceipt receipt)
        {
            if (receipt.Logs == null || receipt.Logs.Length == 0)
            {
                return;
            }

            var logs = _logs.GetOrAdd(filter.Id, i => new List<FilterLog>());
            for (var i = 0; i < receipt.Logs.Length; i++)
            {
                var logEntry = receipt.Logs[i];
                var filterLog = CreateLog(filter, receipt, logEntry);
                if (!(filterLog is null))
                {
                    logs.Add(filterLog);
                }
            }

            if (logs.Count == 0)
            {
                return;
            }

            _logger.Debug($"Filter with id: '{filter.Id}' contains {logs.Count} logs.");
        }

        private FilterLog CreateLog(LogFilter logFilter, TransactionReceipt receipt, LogEntry logEntry)
        {
            if (logFilter.FromBlock.Type == FilterBlockType.BlockId &&
                logFilter.FromBlock.BlockId > receipt.BlockNumber)
            {
                return null;
            }

            if (logFilter.ToBlock.Type == FilterBlockType.BlockId && logFilter.ToBlock.BlockId < receipt.BlockNumber)
            {
                return null;
            }

            if (!logFilter.Accepts(logEntry))
            {
                return null;
            }

            if (logFilter.FromBlock.Type == FilterBlockType.Earliest
                || logFilter.FromBlock.Type == FilterBlockType.Pending
                || logFilter.ToBlock.Type == FilterBlockType.Earliest
                || logFilter.ToBlock.Type == FilterBlockType.Pending)
            {
                return CreateLog(UInt256.One, receipt, logEntry);
            }

            if (logFilter.FromBlock.Type == FilterBlockType.Latest || logFilter.ToBlock.Type == FilterBlockType.Latest)
            {
                //TODO: check if is last mined block
                return CreateLog(UInt256.One, receipt, logEntry);
            }

            return CreateLog(UInt256.One, receipt, logEntry);
        }

        //TODO: Pass a proper log index
        private FilterLog CreateLog(UInt256 logIndex, TransactionReceipt receipt, LogEntry logEntry)
        {
            return new FilterLog(logIndex, receipt.BlockNumber, receipt.BlockHash,
                receipt.Index, receipt.TransactionHash, logEntry.LoggersAddress,
                logEntry.Data, logEntry.Topics);
        }
    }
}