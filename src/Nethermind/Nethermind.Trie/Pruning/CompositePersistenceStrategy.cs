// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Trie.Pruning;

public class CompositePersistenceStrategy : IPersistenceStrategy
{
    private readonly List<IPersistenceStrategy> _strategies = [];

    public CompositePersistenceStrategy(params IPersistenceStrategy[] strategies) => _strategies.AddRange(strategies);

    public IPersistenceStrategy AddStrategy(IPersistenceStrategy strategy)
    {
        _strategies.Add(strategy);
        return this;
    }

    public bool ShouldPersist(long blockNumber)
    {
        for (int i = 0; i < _strategies.Count; i++)
        {
            if (_strategies[i].ShouldPersist(blockNumber))
            {
                return true;
            }
        }

        return false;
    }
}
