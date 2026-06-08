// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;

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

    public bool ShouldPersist(ulong blockNumber) => _strategies.Any(strategy => strategy.ShouldPersist(blockNumber));
}
