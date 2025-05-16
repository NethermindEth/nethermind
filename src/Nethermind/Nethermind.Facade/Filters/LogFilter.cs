// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;

namespace Nethermind.Blockchain.Filters
{
    public class LogFilter(
        int id,
        BlockParameter fromBlock,
        BlockParameter toBlock,
        AddressFilter addressFilter,
        TopicsFilter topicsFilter)
        : FilterBase(id)
    {
        public AddressFilter AddressFilter { get; } = addressFilter;
        public TopicsFilter TopicsFilter { get; } = topicsFilter;
        public BlockParameter FromBlock { get; } = fromBlock;
        public BlockParameter ToBlock { get; } = toBlock;

        public bool Accepts(LogEntry logEntry) => AddressFilter.Accepts(logEntry.Address) && TopicsFilter.Accepts(logEntry);

        public bool Matches(Core.Bloom bloom) => AddressFilter.Matches(bloom) && TopicsFilter.Matches(bloom);

        public bool Matches(ref BloomStructRef bloom) => AddressFilter.Matches(ref bloom) && TopicsFilter.Matches(ref bloom);

        public bool Accepts(ref LogEntryStructRef logEntry) => AddressFilter.Accepts(ref logEntry.Address) && TopicsFilter.Accepts(ref logEntry);
    }
}
