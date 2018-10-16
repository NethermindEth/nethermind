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

using System.Linq;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core;

namespace Nethermind.Blockchain.Filters
{
    public class Filter : FilterBase
    {
        private readonly TopicsFilter _topicsFilter;
        public FilterBlock FromBlock { get; }
        public FilterBlock ToBlock { get; }
        public FilterAddress Address { get; }
        
        public Filter(int id, FilterBlock fromBlock, FilterBlock toBlock,
            FilterAddress address, TopicsFilter topicsFilter) : base(id)
        {
            FromBlock = fromBlock;
            ToBlock = toBlock;
            Address = address;
            _topicsFilter = topicsFilter;
        }

        public bool Accepts(LogEntry logEntry)
        {
            if (Address.Address != null && Address.Address != logEntry.LoggersAddress)
            {
                return false;
            }
            
            if (Address.Addresses != null && Address.Addresses.All(a => a != logEntry.LoggersAddress))
            {
                return false;
            }

            return _topicsFilter.Accepts(logEntry);
        }
    }
}