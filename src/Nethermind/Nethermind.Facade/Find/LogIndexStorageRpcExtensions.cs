// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db.LogIndex;

namespace Nethermind.Facade.Find;

public static class LogIndexStorageRpcExtensions
{
    // Done sequentially, as with a single address/topic fetching averaging at 0.01s,
    // using parallelization here introduces more problems than it solves.
    public static IList<int> GetBlockNumbersFor(this ILogIndexStorage storage,
        LogFilter filter, long fromBlock, long toBlock,
        CancellationToken cancellationToken = default)
    {
        (int from, int to) = ((int)fromBlock, (int)toBlock);

        IList<LogPosition>? addressPositions = null;
        if (filter.AddressFilter.Addresses is { Count: > 0 } addresses)
            addressPositions = AscListHelper.UnionAll(addresses.Select(a => storage.GetLogPositions(a, from, to)));

        // TODO: consider passing storage directly to keep abstractions
        var topicIndex = 0;
        Dictionary<Hash256, IList<LogPosition>>[]? byTopic = null;
        foreach (TopicExpression expression in filter.TopicsFilter.Expressions)
        {
            byTopic ??= new Dictionary<Hash256, IList<LogPosition>>[LogIndexStorage.MaxTopics];
            byTopic[topicIndex] = new();

            foreach (Hash256 topic in expression.Topics)
            {
                var i = topicIndex;
                byTopic[topicIndex].GetOrAdd(topic, _ => storage.GetLogPositions(i, topic, from, to));
            }

            topicIndex++;
        }

        if (byTopic is null)
            return addressPositions?.ToBlockNumbers() ?? [];

        // ReSharper disable once CoVariantArrayConversion
        IList<LogPosition> topicPositions = filter.TopicsFilter.FilterPositions(byTopic);

        if (addressPositions is null)
            return topicPositions.ToBlockNumbers();

        return AscListHelper.Intersect(addressPositions, topicPositions).ToBlockNumbers();
    }

    private static IList<int> ToBlockNumbers(this IList<LogPosition> positions)
    {
        var blockNumbers = new List<int>();
        foreach (LogPosition position in positions)
        {
            if (blockNumbers.Count is 0 || blockNumbers[^1] != position.BlockNumber)
                blockNumbers.Add(position.BlockNumber);
        }

        return blockNumbers;
    }
}
