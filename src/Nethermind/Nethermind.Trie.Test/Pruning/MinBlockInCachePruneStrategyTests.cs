// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class MinBlockInCachePruneStrategyTests
    {
        private IPruningStrategy _baseStrategy;
        private MinBlockInCachePruneStrategy _strategy;
        private const ulong MinBlockFromPersisted = 5;
        private const ulong PruneBoundary = 32;

        [SetUp]
        public void Setup()
        {
            _baseStrategy = Substitute.For<IPruningStrategy>();
            _strategy = new MinBlockInCachePruneStrategy(_baseStrategy, MinBlockFromPersisted, PruneBoundary);
        }

        [Test]
        public void ShouldPruneDirtyNode_should_return_false_when_block_difference_is_less_than_min_block_from_persisted()
        {
            ulong latestCommittedBlock = 100;
            ulong lastPersistedBlock = latestCommittedBlock - PruneBoundary - MinBlockFromPersisted + 1;
            TrieStoreState state = new(100, 200, latestCommittedBlock, lastPersistedBlock);

            _baseStrategy.ShouldPruneDirtyNode(state).Returns(true);
            Assert.That(_strategy.ShouldPruneDirtyNode(state), Is.False);
        }

        [Test]
        public void ShouldPruneDirtyNode_should_delegate_to_base_strategy_when_block_difference_is_greater_than_or_equal_to_min_block_from_persisted()
        {
            ulong latestCommittedBlock = 100;
            ulong lastPersistedBlock = latestCommittedBlock - PruneBoundary - MinBlockFromPersisted;
            TrieStoreState state = new(100, 200, latestCommittedBlock, lastPersistedBlock);

            _baseStrategy.ShouldPruneDirtyNode(state).Returns(true);
            Assert.That(_strategy.ShouldPruneDirtyNode(state), Is.True);
        }

        // Regression: ulong subtraction must not wrap. The block count since last persist is
        // below the boundary (so master returned false), but a naive ulong subtraction underflows
        // to near-ulong.MaxValue and would let the base strategy prune.
        [TestCase(10ul, 0ul, TestName = "latest committed block below prune boundary")]
        [TestCase(40ul, 20ul, TestName = "last persisted block ahead of reorg boundary")]
        public void ShouldPruneDirtyNode_should_return_false_without_underflow_when_few_blocks_since_persist(ulong latestCommittedBlock, ulong lastPersistedBlock)
        {
            TrieStoreState state = new(100, 200, latestCommittedBlock, lastPersistedBlock);

            _baseStrategy.ShouldPruneDirtyNode(state).Returns(true);
            Assert.That(_strategy.ShouldPruneDirtyNode(state), Is.False);
        }
    }
}
