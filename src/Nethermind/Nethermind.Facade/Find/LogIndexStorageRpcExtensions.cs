// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Facade.Find;

public static class LogIndexStorageRpcExtensions
{
    public static List<int> GetBlockNumbersFor(this ILogIndexStorage storage, LogFilter filter, long fromBlock, long toBlock, CancellationToken cancellationToken = default)
    {
        ConcurrentDictionary<Address, List<int>> byAddress = null;
        if (filter.AddressFilter.Address is { } address)
        {
            byAddress = new() { [address] = null };
        }
        else if (filter.AddressFilter.Addresses is { Count: > 0 } addresses)
        {
            byAddress = new();
            byAddress.AddRange(addresses.Select(a => KeyValuePair.Create(a.Value, (List<int>)null)));
        }

        ConcurrentDictionary<Hash256, List<int>> byTopic = null;
        foreach (Hash256 topic in filter.TopicsFilter.Topics)
        {
            byTopic ??= new();
            byTopic[topic] = null;
        }

        Enumerable.Empty<object>()
            .Union(byAddress?.Keys ?? Enumerable.Empty<Address>())
            .Union(byTopic?.Keys ?? Enumerable.Empty<Hash256>())
            .AsParallel() // TODO utilize canRunParallel from LogFinder?
            .ForAll(x =>
            {
                if (x is Address addr)
                    byAddress![addr] = storage.GetBlockNumbersFor(addr, (int)fromBlock, (int)toBlock);
                if (x is Hash256 tpc)
                    byTopic![tpc] = storage.GetBlockNumbersFor(tpc, (int)fromBlock, (int)toBlock);
            });

        if (byTopic is null)
            return AscListHelper.UnionAll(byAddress?.Values ?? []);

        List<int> blockNumbers = filter.TopicsFilter.FilterBlockNumbers(byTopic);

        if (byAddress is null)
            return blockNumbers;

        blockNumbers = AscListHelper.Intersect(
            AscListHelper.UnionAll(byAddress.Values),
            blockNumbers
        );

        return blockNumbers;
    }

}
