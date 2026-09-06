// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Db.FullPruning;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.FullPruning
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class PruningTriggerPruningStrategyTests
    {
        private const ulong PruningBoundary = 128; // Pruning.PruningBoundary with snap serving (SnapServingMaxDepth); minimum enforced is 64

        private IFullPruningDb _fullPruningDb;
        private IPruningStrategy _basePruningStrategy;
        private PruningTriggerPruningStrategy _strategy;

        [SetUp]
        public void Setup()
        {
            _fullPruningDb = Substitute.For<IFullPruningDb>();
            _basePruningStrategy = Substitute.For<IPruningStrategy>();
            _strategy = new PruningTriggerPruningStrategy(_fullPruningDb, _basePruningStrategy, PruningBoundary);
        }

        [TearDown]
        public void TearDown() => _strategy.Dispose();

        [Test]
        public void DeleteObsoleteKeys_should_return_false_when_in_pruning()
        {
            _basePruningStrategy.DeleteObsoleteKeys.Returns(true);
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            Assert.That(_strategy.DeleteObsoleteKeys, Is.False);
        }

        [Test]
        public void DeleteObsoleteKeys_should_return_base_strategy_value_after_pruning_finished()
        {
            _basePruningStrategy.DeleteObsoleteKeys.Returns(true);
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            _fullPruningDb.PruningFinished += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            Assert.That(_strategy.DeleteObsoleteKeys, Is.True);
        }

        [Test]
        public void ShouldPruneDirtyNode_should_return_true_when_in_pruning_and_difference_greater_than_32()
        {
            TrieStoreState state = new(100, 200, 500, 300); // (LatestCommittedBlock - PruningBoundary) - LastPersistedBlock = 72 > 32
            _basePruningStrategy.ShouldPruneDirtyNode(state).Returns(false);
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            Assert.That(_strategy.ShouldPruneDirtyNode(state), Is.True);
        }

        [Test]
        public void ShouldPruneDirtyNode_should_not_fire_when_in_pruning_and_only_the_pruning_boundary_separates_head_from_persisted()
        {
            // A snapshot can only persist blocks older than the pruning boundary, so right after a snapshot
            // LastPersistedBlock == LatestCommittedBlock - PruningBoundary. Nothing new is persistable here,
            // so the full-pruning trigger must defer to the base strategy instead of forcing a prune.
            TrieStoreState state = new(100, 200, 1000, 1000 - PruningBoundary);
            _basePruningStrategy.ShouldPruneDirtyNode(Arg.Any<TrieStoreState>()).Returns(false);
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            Assert.That(_strategy.ShouldPruneDirtyNode(state), Is.False);
        }

        [Test]
        public void ShouldPruneDirtyNode_should_fire_once_per_32_blocks_not_once_per_block_when_in_pruning()
        {
            // Simulate a node at head during full pruning: each block commit evaluates the strategy and,
            // when it fires, a snapshot persists everything up to LatestCommittedBlock - PruningBoundary.
            _basePruningStrategy.ShouldPruneDirtyNode(Arg.Any<TrieStoreState>()).Returns(false);
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));

            const ulong startBlock = 1000;
            const int blocks = 256;
            ulong lastPersisted = startBlock - PruningBoundary;
            int prunes = 0;
            for (ulong block = startBlock + 1; block <= startBlock + blocks; block++)
            {
                if (_strategy.ShouldPruneDirtyNode(new TrieStoreState(100, 200, block, lastPersisted)))
                {
                    prunes++;
                    lastPersisted = block - PruningBoundary;
                }
            }

            // Every 33rd block (1033, 1066, ..., 1231) has 33 > 32 persistable blocks behind the boundary: 7 snapshots.
            // A head-relative trigger fires on all 256 blocks, each persisting a single block.
            Assert.That(prunes, Is.EqualTo(7));
        }
    }
}
