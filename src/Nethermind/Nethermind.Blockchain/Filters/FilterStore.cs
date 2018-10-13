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
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Filters
{
    public class FilterStore : IFilterStore
    {
        private readonly ConcurrentDictionary<int, FilterBase> _filters = new ConcurrentDictionary<int, FilterBase>();

        public IReadOnlyCollection<Filter> GetAll() => _filters.Select(f => f.Value).OfType<Filter>().ToList();

        public BlockFilter CreateBlockFilter(UInt256 startBlockNumber)
        {
            BlockFilter blockFilter = new BlockFilter(startBlockNumber);
            AddFilter(blockFilter);
            return blockFilter;
        }

        public Filter CreateFilter(FilterBlock fromBlock, FilterBlock toBlock, object address = null, IEnumerable<object> topics = null)
        {
            var filter = new Filter
            {
                FromBlock = fromBlock,
                ToBlock = toBlock,
                Address = GetAddress(address),
                Topics = GetTopics(topics),
            };
            AddFilter(filter);

            return filter;
        }
        
        private void AddFilter(FilterBase filter)
        {
            filter.FilterId = _filters.Any() ? _filters.Max(f => f.Key) + 1 : 1;
            _filters[filter.FilterId] = filter;
        }

        private static FilterAddress GetAddress(object address)
            => address is null
                ? null
                : new FilterAddress
                {
                    Address = address is string s ? new Address(s) : null,
                    Addresses = address is IEnumerable<string> e ? e.Select(a => new Address(a)).ToList() : null
                };

        private static IEnumerable<FilterTopic> GetTopics(IEnumerable<object> topics)
            => topics?.Select(GetTopic);

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
            var first = topics?.FirstOrDefault();
            var second = topics?.Skip(1).FirstOrDefault();

            return new FilterTopic
            {
                First = first is null ? null : new Keccak(first),
                Second = second is null ? null : new Keccak(second),
            };
        }
    }
}