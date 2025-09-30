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
using Nethermind.Db;

namespace Nethermind.Facade.Find;

public static class LogIndexStorageRpcExtensions
{
    // Done sequentially, as with a single address/topic fetching averaging at 0.01s,
    // using parallelization here introduces more problems than it solves.
    public static List<int> GetBlockNumbersFor(this ILogIndexStorage storage,
        LogFilter filter, long fromBlock, long toBlock,
        CancellationToken cancellationToken = default)
    {
        (int from, int to) = ((int)fromBlock, (int)toBlock);

        List<int>? addressNumbers = null;
        if (filter.AddressFilter.Address is { } address)
            addressNumbers = storage.GetBlockNumbersFor(address, from, to);
        else if (filter.AddressFilter.Addresses is { Count: > 0 } addresses)
            addressNumbers = AscListHelper.UnionAll(addresses.Select(a => storage.GetBlockNumbersFor(a, from, to)));

        // TODO: consider passing storage directly to keep abstractions
        var topicIndex = 0;
        Dictionary<Hash256, List<int>>[]? byTopic = null;
        foreach (TopicExpression expression in filter.TopicsFilter.Expressions)
        {
            byTopic ??= new Dictionary<Hash256, List<int>>[LogIndexStorage.MaxTopics];
            byTopic[topicIndex] = new();

            foreach (Hash256 topic in expression.Topics)
            {
                var i = topicIndex;
                byTopic[topicIndex].GetOrAdd(topic, _ => storage.GetBlockNumbersFor(i, topic, from, to));
            }

            topicIndex++;
        }

        if (byTopic is null)
            return addressNumbers ?? [];

        // ReSharper disable once CoVariantArrayConversion
        List<int> topicNumbers = filter.TopicsFilter.FilterBlockNumbers(byTopic);

        if (addressNumbers is null)
            return topicNumbers;

        return AscListHelper.Intersect(addressNumbers, topicNumbers);
    }
}
