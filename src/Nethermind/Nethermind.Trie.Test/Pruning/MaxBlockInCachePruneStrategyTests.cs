// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning
{
    [TestFixture]
    [Parallelizable(ParallelScope.Self)]
    public class MaxBlockInCachePruneStrategyTests
    {
        private IPruningStrategy _baseStrategy;
        private MaxBlockInCachePruneStrategy _strategy;
        private const long MaxBlockFromPersisted = 10;
        private const long PruneBoundary = 32;

        [SetUp]
        public void Setup()
        {
            _baseStrategy = Substitute.For<IPruningStrategy>();
            _strategy = new MaxBlockInCachePruneStrategy(_baseStrategy, MaxBlockFromPersisted, PruneBoundary);
        }

        [Test]
        public void ShouldPruneDirtyNode_should_return_true_when_block_difference_exceeds_max_block_from_persisted()
        {
            long latestCommittedBlock = 100;
            long lastPersistedBlock = latestCommittedBlock - PruneBoundary - MaxBlockFromPersisted;
            var state = new TrieStoreState(100, 200, latestCommittedBlock, lastPersistedBlock);

            _baseStrategy.ShouldPruneDirtyNode(state).Returns(false);
            _strategy.ShouldPruneDirtyNode(state).Should().BeTrue();
        }

        [Test]
        public void ShouldPruneDirtyNode_should_delegate_to_base_strategy_when_block_difference_is_less_than_max_block_from_persisted()
        {
            long latestCommittedBlock = 100;
            long lastPersistedBlock = latestCommittedBlock - PruneBoundary - MaxBlockFromPersisted + 1;
            var state = new TrieStoreState(100, 200, latestCommittedBlock, lastPersistedBlock);

            _baseStrategy.ShouldPruneDirtyNode(state).Returns(true);
            _strategy.ShouldPruneDirtyNode(state).Should().BeTrue();
            _baseStrategy.ShouldPruneDirtyNode(state).Returns(false);
            _strategy.ShouldPruneDirtyNode(state).Should().BeFalse();
        }
    }
}
