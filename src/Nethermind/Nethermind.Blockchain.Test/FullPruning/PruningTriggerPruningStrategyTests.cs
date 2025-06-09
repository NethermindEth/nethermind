// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Db.FullPruning;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.FullPruning
{
    [TestFixture]
    [Parallelizable(ParallelScope.Self)]
    public class PruningTriggerPruningStrategyTests
    {
        private IFullPruningDb _fullPruningDb;
        private IPruningStrategy _basePruningStrategy;
        private PruningTriggerPruningStrategy _strategy;

        [SetUp]
        public void Setup()
        {
            _fullPruningDb = Substitute.For<IFullPruningDb>();
            _basePruningStrategy = Substitute.For<IPruningStrategy>();
            _strategy = new PruningTriggerPruningStrategy(_fullPruningDb, _basePruningStrategy);
        }

        [TearDown]
        public void TearDown()
        {
            _strategy.Dispose();
        }

        [Test]
        public void DeleteObsoleteKeys_should_return_false_when_in_pruning()
        {
            _basePruningStrategy.DeleteObsoleteKeys.Returns(true);
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            _strategy.DeleteObsoleteKeys.Should().BeFalse();
        }

        [Test]
        public void DeleteObsoleteKeys_should_return_base_strategy_value_after_pruning_finished()
        {
            _basePruningStrategy.DeleteObsoleteKeys.Returns(true);
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            _fullPruningDb.PruningFinished += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            _strategy.DeleteObsoleteKeys.Should().BeTrue();
        }

        [Test]
        public void ShouldPruneDirtyNode_should_return_true_when_in_pruning_and_difference_greater_than_32()
        {
            var state = new TrieStoreState(100, 200, 300, 250); // LatestCommittedBlock - LastPersistedBlock = 50 > 32
            _basePruningStrategy.ShouldPruneDirtyNode(state).Returns(false);
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            _strategy.ShouldPruneDirtyNode(state).Should().BeTrue();
        }
    }
}
