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
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Filters
{
    public class FilterStore : IFilterStore
    {
        private readonly ConcurrentDictionary<int, FilterBase> _filters = new ConcurrentDictionary<int, FilterBase>();

        public IReadOnlyCollection<Filter> GetAll()
        {
            return _filters.Select(f => f.Value).OfType<Filter>().ToList();
        }

        public BlockFilter CreateBlockFilter(UInt256 startBlockNumber)
        {
            BlockFilter blockFilter = new BlockFilter(GetFilterId(), startBlockNumber);
            AddFilter(blockFilter);
            return blockFilter;
        }

        public Filter CreateFilter(FilterBlock fromBlock, FilterBlock toBlock, 
            object address = null, IEnumerable<object> topics = null)
        {
            var filter = new Filter(GetFilterId(), fromBlock, toBlock, 
                GetAddress(address), GetTopicsFilter(topics));
            AddFilter(filter);

            return filter;
        }

        public void RemoveFilter(int filterId)
        {
            _filters.TryRemove(filterId, out _);
        }

        private void AddFilter(FilterBase filter)
        {
            _filters[filter.Id] = filter;
        }

        private int GetFilterId() => _filters.Any() ? _filters.Max(f => f.Key) + 1 : 1;
        
        private TopicsFilter GetTopicsFilter(IEnumerable<object> topics = null)
        {
            var filterTopics = GetFilterTopics(topics);
            var expressions = new List<TopicExpression>();

            for (int i = 0; i < filterTopics.Length; i++)
            {
                var filterTopic = filterTopics[i];
                var orExpression = new OrExpression(new[]
                {
                    GetTopicExpression(filterTopic.First),
                    GetTopicExpression(filterTopic.Second)
                });
                expressions.Add(orExpression);
            }

            return new TopicsFilter(expressions.ToArray());
        }
        
        private TopicExpression GetTopicExpression(Keccak topic)
        {
            if (topic == null)
            {
                return new AnyTopic();
            }

            return new SpecificTopic(topic);
        }

        private static FilterAddress GetAddress(object address)
        {
            return address is null
                ? null
                : new FilterAddress
                {
                    Address = address is string s ? new Address(s) : null,
                    Addresses = address is IEnumerable<string> e ? e.Select(a => new Address(a)).ToList() : null
                };
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
                        First = new Keccak(topic)
                    };
            }

            var topics = (obj as IEnumerable<string>)?.ToList();
            string first = topics?.FirstOrDefault();
            string second = topics?.Skip(1).FirstOrDefault();

            return new FilterTopic
            {
                First = first is null ? null : new Keccak(first),
                Second = second is null ? null : new Keccak(second)
            };
        }
    }
}