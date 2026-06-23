// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning;

public class CompositePersistenceStrategyTests
{
    [TestCase(3, ExpectedResult = true)]
    [TestCase(4, ExpectedResult = true)]
    [TestCase(5, ExpectedResult = false)]
    public bool Should_persist_when_any_inner_strategy_matches(long blockNumber)
        => new CompositePersistenceStrategy(new ConstantInterval(2), new ConstantInterval(3)).ShouldPersist(blockNumber);
}
