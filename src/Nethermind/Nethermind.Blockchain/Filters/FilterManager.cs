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
            foreach (var logEntry in receiptContext.Receipt.Logs)
            {
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
            if (filter.FromBlock != receiptContext.BlockHash && filter.ToBlock != receiptContext.BlockHash)
            {
                return null;
            }

            var address = GetAddress(filter, logEntry);
            if (address == null)
            {
                return null;
            }

            var topics = GetTopics(filter, logEntry);

            return new FilterLog(receiptContext.LogIndex, receiptContext.BlockNumber, receiptContext.BlockHash,
                receiptContext.TransactionIndex, receiptContext.TransactionHash, address, logEntry.Data, topics);
        }

        private Address GetAddress(Filter filter, LogEntry logEntry)
        {
            if (filter.Address == null)
            {
                return logEntry.LoggersAddress;
            }

            if (filter.Address.Address != null && filter.Address.Address == logEntry.LoggersAddress)
            {
                return logEntry.LoggersAddress;
            }

            if (filter.Address.Addresses == null || !filter.Address.Addresses.Any())
            {
                return logEntry.LoggersAddress;
            }

            return filter.Address.Addresses.SingleOrDefault(a => a == logEntry.LoggersAddress);
        }

        private Keccak[] GetTopics(Filter filter, LogEntry logEntry)
        {
            if (filter.Topics == null || !filter.Topics.Any())
            {
                return logEntry.Topics;
            }

            var foundTopics = new List<Keccak>();
            var index = 0;
            foreach (var filterTopic in filter.Topics)
            {
                if (logEntry.Topics.Length == index)
                {
                    return foundTopics.ToArray();
                }

                var foundTopic = GetTopic(filterTopic, logEntry.Topics[index]);
                foundTopics.Add(foundTopic);
                index++;
            }

            return foundTopics.ToArray();
        }

        private Keccak GetTopic(FilterTopic filterTopic, Keccak topic)
        {
            if (filterTopic == null || (filterTopic.First == null && filterTopic.Second == null))
            {
                return topic;
            }

            var firstTopic = GetValidTopic(filterTopic.First, topic);

            return firstTopic != null ? firstTopic : GetValidTopic(filterTopic.Second, topic);
        }

        private Keccak GetValidTopic(Keccak topicToFilter, Keccak logEntryTopic)
            => topicToFilter == null || topicToFilter == logEntryTopic ? logEntryTopic : null;
    }
}