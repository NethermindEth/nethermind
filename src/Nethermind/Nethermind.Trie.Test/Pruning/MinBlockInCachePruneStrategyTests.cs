// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning
{
    [TestFixture]
    [Parallelizable(ParallelScope.Self)]
    public class MinBlockInCachePruneStrategyTests
    {
        private IPruningStrategy _baseStrategy;
        private MinBlockInCachePruneStrategy _strategy;
        private const long MinBlockFromPersisted = 5;
        private const long PruneBoundary = 32;

        [SetUp]
        public void Setup()
        {
            _baseStrategy = Substitute.For<IPruningStrategy>();
            _strategy = new MinBlockInCachePruneStrategy(_baseStrategy, MinBlockFromPersisted, PruneBoundary);
        }

        [Test]
        public void ShouldPruneDirtyNode_should_return_false_when_block_difference_is_less_than_min_block_from_persisted()
        {
            long latestCommittedBlock = 100;
            long lastPersistedBlock = latestCommittedBlock - PruneBoundary - MinBlockFromPersisted + 1;
            var state = new TrieStoreState(100, 200, latestCommittedBlock, lastPersistedBlock);

            _baseStrategy.ShouldPruneDirtyNode(state).Returns(true);
            _strategy.ShouldPruneDirtyNode(state).Should().BeFalse();
        }

        [Test]
        public void ShouldPruneDirtyNode_should_delegate_to_base_strategy_when_block_difference_is_greater_than_or_equal_to_min_block_from_persisted()
        {
            long latestCommittedBlock = 100;
            long lastPersistedBlock = latestCommittedBlock - PruneBoundary - MinBlockFromPersisted;
            var state = new TrieStoreState(100, 200, latestCommittedBlock, lastPersistedBlock);

            _baseStrategy.ShouldPruneDirtyNode(state).Returns(true);
            _strategy.ShouldPruneDirtyNode(state).Should().BeTrue();
        }
    }
}
