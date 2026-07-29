// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class MaxBlockInCachePruneStrategyTests
    {
        private IPruningStrategy _baseStrategy;
        private MaxBlockInCachePruneStrategy _strategy;
        private const ulong MaxBlockFromPersisted = 10;
        private const ulong PruneBoundary = 32;

        [SetUp]
        public void Setup()
        {
            _baseStrategy = Substitute.For<IPruningStrategy>();
            _strategy = new MaxBlockInCachePruneStrategy(_baseStrategy, MaxBlockFromPersisted, PruneBoundary);
        }

        [Test]
        public void ShouldPruneDirtyNode_should_return_true_when_block_difference_exceeds_max_block_from_persisted()
        {
            ulong latestCommittedBlock = 100;
            ulong lastPersistedBlock = latestCommittedBlock - PruneBoundary - MaxBlockFromPersisted;
            TrieStoreState state = new(100, 200, latestCommittedBlock, lastPersistedBlock);

            _baseStrategy.ShouldPruneDirtyNode(state).Returns(false);
            Assert.That(_strategy.ShouldPruneDirtyNode(state), Is.True);
        }

        [Test]
        public void ShouldPruneDirtyNode_should_delegate_to_base_strategy_when_block_difference_is_less_than_max_block_from_persisted()
        {
            ulong latestCommittedBlock = 100;
            ulong lastPersistedBlock = latestCommittedBlock - PruneBoundary - MaxBlockFromPersisted + 1;
            TrieStoreState state = new(100, 200, latestCommittedBlock, lastPersistedBlock);

            _baseStrategy.ShouldPruneDirtyNode(state).Returns(true);
            Assert.That(_strategy.ShouldPruneDirtyNode(state), Is.True);
            _baseStrategy.ShouldPruneDirtyNode(state).Returns(false);
            Assert.That(_strategy.ShouldPruneDirtyNode(state), Is.False);
        }

        // Regression: ulong subtraction must not wrap. The block count since last persist is
        // below the max (so master delegated to the base strategy), but a naive ulong subtraction
        // underflows to near-ulong.MaxValue and would force a prune on every low block.
        [TestCase(10ul, 0ul, TestName = "latest committed block below prune boundary")]
        [TestCase(40ul, 20ul, TestName = "last persisted block ahead of reorg boundary")]
        public void ShouldPruneDirtyNode_should_not_force_prune_without_underflow_when_few_blocks_since_persist(ulong latestCommittedBlock, ulong lastPersistedBlock)
        {
            TrieStoreState state = new(100, 200, latestCommittedBlock, lastPersistedBlock);

            _baseStrategy.ShouldPruneDirtyNode(state).Returns(false);
            Assert.That(_strategy.ShouldPruneDirtyNode(state), Is.False);
        }
    }
}
