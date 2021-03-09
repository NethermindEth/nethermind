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
using System.IO;
using System.Linq;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters
{
    public class FilterStore : IFilterStore
    {
        private int _nextFilterId;

        private readonly ConcurrentDictionary<int, FilterBase> _filters = new();

        public bool FilterExists(int filterId) => _filters.ContainsKey(filterId);

        public FilterType GetFilterType(int filterId)
        {
            /* so far ok to use block filter if none */
            if (!_filters.TryGetValue(filterId, out var filter))
            {
                return FilterType.BlockFilter;
            }

            switch (filter)
            {
                case LogFilter _: return FilterType.LogFilter;
                case BlockFilter _: return FilterType.BlockFilter;
                case PendingTransactionFilter _: return FilterType.PendingTransactionFilter;
                default: return FilterType.BlockFilter;
            }
        }

        public T[] GetFilters<T>() where T : FilterBase
        {
            return _filters.Select(f => f.Value).OfType<T>().ToArray();
        }

        public BlockFilter CreateBlockFilter(long startBlockNumber, bool setId = true)
        {
            var filterId = setId ? GetFilterId() : 0;
            var blockFilter = new BlockFilter(filterId, startBlockNumber);
            return blockFilter;
        }

        public PendingTransactionFilter CreatePendingTransactionFilter(bool setId = true)
        {
            var filterId = setId ? GetFilterId() : 0;
            var pendingTransactionFilter = new PendingTransactionFilter(filterId);
            return pendingTransactionFilter;
        }

        public LogFilter CreateLogFilter(BlockParameter fromBlock, BlockParameter toBlock,
            object address = null, IEnumerable<object> topics = null, bool setId = true)
        {
            var filterId = setId ? GetFilterId() : 0;
            var filter = new LogFilter(filterId, fromBlock, toBlock,
                GetAddress(address), GetTopicsFilter(topics));

            return filter;
        }

        public void RemoveFilter(int filterId)
        {
            _filters.TryRemove(filterId, out _);
            FilterRemoved?.Invoke(this, new FilterEventArgs(filterId));
        }

        public event EventHandler<FilterEventArgs> FilterRemoved;

        public void SaveFilter(FilterBase filter)
        {
            if (_filters.ContainsKey(filter.Id))
            {
                throw new InvalidOperationException($"Filter with ID {filter.Id} already exists");
            }

            _nextFilterId = Math.Max(filter.Id + 1, _nextFilterId);
            _filters[filter.Id] = filter;
        }

        private int GetFilterId() => _nextFilterId++;

        private SequenceTopicsFilter GetTopicsFilter(IEnumerable<object> topics = null)
        {
            if (topics == null)
            {
                return SequenceTopicsFilter.AnyTopic;
            }

            var filterTopics = GetFilterTopics(topics);
            var expressions = new List<TopicExpression>();

            for (int i = 0; i < filterTopics.Length; i++)
            {
                expressions.Add(GetTopicExpression(filterTopics[i]));
            }

            return new SequenceTopicsFilter(expressions.ToArray());
        }

        private TopicExpression GetTopicExpression(FilterTopic filterTopic)
        {
            if (filterTopic == null)
            {
                return AnyTopic.Instance;
            }
            else if (filterTopic.Topic != null)
            {
                return new SpecificTopic(filterTopic.Topic);
            }
            else if (filterTopic.Topics.Any())
            {
                return new OrExpression(filterTopic.Topics.Select(t => new SpecificTopic(t)).ToArray<TopicExpression>());
            }
            else
            {
                return AnyTopic.Instance; 
            }
        }

        private static AddressFilter GetAddress(object address)
        {
            if (address is null)
            {
                return AddressFilter.AnyAddress; 
            }

            if (address is string s)
            {
                return new AddressFilter(new Address(s));
            }
            
            if (address is IEnumerable<string> e)
            {
                return new AddressFilter(e.Select(a => new Address(a)).ToHashSet());
            }
            
            throw new InvalidDataException("Invalid address filter format");
        }

        private static FilterTopic[] GetFilterTopics(IEnumerable<object> topics)
        {
            return topics?.Select(GetTopic).ToArray();
        }

        private static FilterTopic GetTopic(object obj)
        {
            switch (obj)
            {
                case null:
                    return null;
                case string topic:
                    return new FilterTopic
                    {
                        Topic = new Keccak(topic)
                    };
                case Keccak keccak:
                    return new FilterTopic
                    {
                        Topic = keccak
                    };
            }

            var topics = obj as IEnumerable<string>;
            if (topics == null)
            {
                return null;
            }
            else
            {
                return new FilterTopic
                {
                    Topics = topics.Select(t => new Keccak(t)).ToArray()
                };
            }
        }
        
        private class FilterTopic
        {
            public Keccak Topic { get; set; }
            public Keccak[] Topics { get; set; }
        
        }
    }
}
