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

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters
{
    public class FilterManager : IFilterManager
    {
        private readonly ConcurrentDictionary<int, List<FilterLog>> _logs =
            new ConcurrentDictionary<int, List<FilterLog>>();

        private readonly IFilterStore _filterStore;

        public FilterManager(IFilterStore filterStore)
        {
            _filterStore = filterStore;
        }

        public FilterLog[] GetLogs(int filterId)
            => (_logs.ContainsKey(filterId) ? _logs[filterId] : new List<FilterLog>()).ToArray();

        public void AddTransactionReceipt(TransactionReceiptContext receiptContext)
        {
            var filters = _filterStore.GetAll();
            foreach (var filter in filters)
            {
                StoreLogs(filter, receiptContext);
            }
        }

        private void StoreLogs(Filter filter, TransactionReceiptContext receiptContext)
        {
            var logs = _logs.ContainsKey(filter.FilterId) ? _logs[filter.FilterId] : new List<FilterLog>();
            for (var index = 0; index < receiptContext.Receipt.Logs.Length; index++)
            {
                var logEntry = receiptContext.Receipt.Logs[index];
                var filterLog = CreateLog(filter, receiptContext, logEntry);
                if (!(filterLog is null))
                {
                    logs.Add(filterLog);
                }
            }

            _logs[filter.FilterId] = logs;
        }

        private FilterLog CreateLog(Filter filter, TransactionReceiptContext receiptContext, LogEntry logEntry)
        {
            if (!filter.Accepts(logEntry))
            {
                return null;
            }
            
            if (filter.FromBlock.Type == FilterBlockType.BlockId && filter.FromBlock.BlockId > receiptContext.BlockNumber)
            {
                return null;
            }

            if (filter.ToBlock.Type == FilterBlockType.BlockId && filter.ToBlock.BlockId < receiptContext.BlockNumber)
            {
                return null;
            }

            if (filter.FromBlock.Type == FilterBlockType.Earliest || filter.FromBlock.Type == FilterBlockType.Pending
                                                                || filter.ToBlock.Type == FilterBlockType.Earliest ||
                                                                filter.ToBlock.Type == FilterBlockType.Pending)
            {
                return CreateLog(receiptContext, logEntry);
            }

            if (filter.FromBlock.Type == FilterBlockType.Latest || filter.ToBlock.Type == FilterBlockType.Latest)
            {
                //TODO: check if is last mined block
                return CreateLog(receiptContext, logEntry);
            }

            return null;
        }

        private FilterLog CreateLog(TransactionReceiptContext receiptContext, LogEntry logEntry)
        {
            return new FilterLog(receiptContext.LogIndex, receiptContext.BlockNumber, receiptContext.BlockHash,
                receiptContext.TransactionIndex, receiptContext.TransactionHash, logEntry.LoggersAddress,
                logEntry.Data, logEntry.Topics);
        }
    }
}