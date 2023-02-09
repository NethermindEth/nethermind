// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
