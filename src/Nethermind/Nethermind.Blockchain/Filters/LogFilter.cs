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

using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core;

namespace Nethermind.Blockchain.Filters
{
    public class LogFilter : FilterBase
    {
        public AddressFilter AddressFilter { get; }
        public TopicsFilter TopicsFilter { get; }
        public FilterBlock FromBlock { get; }
        public FilterBlock ToBlock { get; }
        
        public LogFilter(int id, FilterBlock fromBlock, FilterBlock toBlock,
            AddressFilter addressFilter, TopicsFilter topicsFilter) : base(id)
        {
            FromBlock = fromBlock;
            ToBlock = toBlock;
            AddressFilter = addressFilter;
            TopicsFilter = topicsFilter;
        }

        public bool Accepts(LogEntry logEntry) =>
            AddressFilter.Accepts(logEntry.LoggersAddress) && TopicsFilter.Accepts(logEntry);

        public bool Matches(Bloom bloom)
        {
            return AddressFilter.Matches(bloom) && TopicsFilter.Matches(bloom);
        }
    }
}