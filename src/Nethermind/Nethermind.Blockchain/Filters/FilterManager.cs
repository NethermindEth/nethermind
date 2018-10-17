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
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Filters
{
    public class FilterManager : IFilterManager
    {
        private readonly ConcurrentDictionary<int, List<FilterLog>> _logs = new ConcurrentDictionary<int, List<FilterLog>>();
        private readonly IFilterStore _filterStore;
        private readonly IBlockProcessor _blockProcessor;
        private readonly ILogger _logger;

        public FilterManager(IFilterStore filterStore, IBlockProcessor blockProcessor, ILogManager logManager)
        {
            _filterStore = filterStore;
            _blockProcessor = blockProcessor;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockProcessor.TransactionReceiptsCreated += (s,e) => AddTransactionReceipts(e.Receipts);
        }

        public FilterLog[] GetLogs(int filterId)
            => (_logs.ContainsKey(filterId) ? _logs[filterId] : new List<FilterLog>()).ToArray();

        public void AddTransactionReceipts(params TransactionReceipt[] receipts)
        {
            if (receipts == null || receipts.Length == 0)
            {
                return;
            }

            var filters = _filterStore.GetFilters();
            if (filters == null || filters.Length == 0)
            {
                return;
            }

            for (var i = 0; i < receipts.Length; i++)
            {
                StoreLogs(filters, receipts[i]);
            }
        }

        private void StoreLogs(Filter[] filters, TransactionReceipt receipt)
        {
            for (var i = 0; i < filters.Length; i++)
            {
                StoreLogs(filters[i], receipt);
            }
        }

        private void StoreLogs(Filter filter, TransactionReceipt receipt)
        {
            if (receipt.Logs == null || receipt.Logs.Length == 0)
            {
                return;
            }
            
            var logs = _logs.ContainsKey(filter.Id) ? _logs[filter.Id] : new List<FilterLog>();
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

            _logs[filter.Id] = logs;
            _logger.Debug($"Filter with id: '{filter.Id}' contains {logs.Count} logs.");
        }

        private FilterLog CreateLog(Filter filter, TransactionReceipt receipt, LogEntry logEntry)
        {
            if (filter.FromBlock.Type == FilterBlockType.BlockId && filter.FromBlock.BlockId > receipt.BlockNumber)
            {
                return null;
            }

            if (filter.ToBlock.Type == FilterBlockType.BlockId && filter.ToBlock.BlockId < receipt.BlockNumber)
            {
                return null;
            }

            if (!filter.Accepts(logEntry))
            {
                return null;
            }

            if (filter.FromBlock.Type == FilterBlockType.Earliest || filter.FromBlock.Type == FilterBlockType.Pending
                                                                || filter.ToBlock.Type == FilterBlockType.Earliest ||
                                                                filter.ToBlock.Type == FilterBlockType.Pending)
            {
                return CreateLog(UInt256.One, receipt, logEntry);
            }

            if (filter.FromBlock.Type == FilterBlockType.Latest || filter.ToBlock.Type == FilterBlockType.Latest)
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