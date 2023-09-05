// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Trie.Pruning;

public class CompositePersistenceStrategy : IPersistenceStrategy
{
    private readonly List<IPersistenceStrategy> _strategies = new();

    public CompositePersistenceStrategy(params IPersistenceStrategy[] strategies)
    {
        _strategies.AddRange(strategies);
    }

    public IPersistenceStrategy AddStrategy(IPersistenceStrategy strategy)
    {
        _strategies.Add(strategy);
        return this;
    }

    public bool ShouldPersist(long blockNumber) => _strategies.Any(strategy => strategy.ShouldPersist(blockNumber));

    public bool ShouldPersist(long currentBlockNumber, out long targetBlockNumber)
    {
        targetBlockNumber = -1;
        foreach (IPersistenceStrategy strategy in _strategies)
        {
            if (strategy.ShouldPersist(currentBlockNumber, out long localTarget))
            {
                if (localTarget < targetBlockNumber)
                    targetBlockNumber = localTarget;
            }
        }
        return targetBlockNumber > -1;
    }
}
