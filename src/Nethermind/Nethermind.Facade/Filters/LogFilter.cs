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

using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;

namespace Nethermind.Blockchain.Filters
{
    public class LogFilter : FilterBase
    {
        public AddressFilter AddressFilter { get; }
        public TopicsFilter TopicsFilter { get; }
        public BlockParameter FromBlock { get; }
        public BlockParameter ToBlock { get; }
        
        public LogFilter(int id, BlockParameter fromBlock, BlockParameter toBlock,
            AddressFilter addressFilter, TopicsFilter topicsFilter) : base(id)
        {
            FromBlock = fromBlock;
            ToBlock = toBlock;
            AddressFilter = addressFilter;
            TopicsFilter = topicsFilter;
        }

        public bool Accepts(LogEntry logEntry) => AddressFilter.Accepts(logEntry.LoggersAddress) && TopicsFilter.Accepts(logEntry);

        public bool Matches(Core.Bloom bloom) => AddressFilter.Matches(bloom) && TopicsFilter.Matches(bloom);

        public bool Matches(ref BloomStructRef bloom) => AddressFilter.Matches(ref bloom) && TopicsFilter.Matches(ref bloom);

        public bool Accepts(ref LogEntryStructRef logEntry) => AddressFilter.Accepts(ref logEntry.LoggersAddress) && TopicsFilter.Accepts(ref logEntry); 
    }
}
