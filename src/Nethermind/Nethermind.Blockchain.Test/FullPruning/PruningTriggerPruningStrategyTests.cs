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
    [Parallelizable(ParallelScope.All)]
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
        public void Constructor_should_subscribe_to_events()
        {
            // Assert
            _fullPruningDb.Received(1).PruningStarted += Arg.Any<EventHandler<PruningEventArgs>>();
            _fullPruningDb.Received(1).PruningFinished += Arg.Any<EventHandler<PruningEventArgs>>();
        }

        [Test]
        public void Dispose_should_unsubscribe_from_events()
        {
            // Act
            _strategy.Dispose();

            // Assert
            _fullPruningDb.Received(1).PruningStarted -= Arg.Any<EventHandler<PruningEventArgs>>();
            _fullPruningDb.Received(1).PruningFinished -= Arg.Any<EventHandler<PruningEventArgs>>();
        }

        [Test]
        public void DeleteObsoleteKeys_should_return_base_strategy_value_when_not_in_pruning()
        {
            // Arrange
            _basePruningStrategy.DeleteObsoleteKeys.Returns(true);

            // Act
            bool result = _strategy.DeleteObsoleteKeys;

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void DeleteObsoleteKeys_should_return_false_when_in_pruning()
        {
            // Arrange
            _basePruningStrategy.DeleteObsoleteKeys.Returns(true);
            
            // Trigger pruning started event
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));

            // Act
            bool result = _strategy.DeleteObsoleteKeys;

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void DeleteObsoleteKeys_should_return_base_strategy_value_after_pruning_finished()
        {
            // Arrange
            _basePruningStrategy.DeleteObsoleteKeys.Returns(true);
            
            // Trigger pruning started event
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            
            // Trigger pruning finished event
            _fullPruningDb.PruningFinished += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));

            // Act
            bool result = _strategy.DeleteObsoleteKeys;

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void ShouldPruneDirtyNode_should_delegate_to_base_strategy_when_not_in_pruning()
        {
            // Arrange
            var state = new TrieStoreState(100, 200, 300, 250);
            _basePruningStrategy.ShouldPruneDirtyNode(state).Returns(true);

            // Act
            bool result = _strategy.ShouldPruneDirtyNode(state);

            // Assert
            result.Should().BeTrue();
            _basePruningStrategy.Received(1).ShouldPruneDirtyNode(state);
        }

        [Test]
        public void ShouldPruneDirtyNode_should_return_true_when_in_pruning_and_difference_greater_than_32()
        {
            // Arrange
            var state = new TrieStoreState(100, 200, 300, 250); // LatestCommittedBlock - LastPersistedBlock = 50 > 32
            _basePruningStrategy.ShouldPruneDirtyNode(state).Returns(false);
            
            // Trigger pruning started event
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));

            // Act
            bool result = _strategy.ShouldPruneDirtyNode(state);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void ShouldPruneDirtyNode_should_delegate_to_base_strategy_when_in_pruning_and_difference_less_than_32()
        {
            // Arrange
            var state = new TrieStoreState(100, 200, 280, 250); // LatestCommittedBlock - LastPersistedBlock = 30 < 32
            _basePruningStrategy.ShouldPruneDirtyNode(state).Returns(false);
            
            // Trigger pruning started event
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));

            // Act
            bool result = _strategy.ShouldPruneDirtyNode(state);

            // Assert
            result.Should().BeFalse();
            _basePruningStrategy.Received(1).ShouldPruneDirtyNode(state);
        }

        [Test]
        public void ShouldPrunePersistedNode_should_always_delegate_to_base_strategy()
        {
            // Arrange
            var state = new TrieStoreState(100, 200, 300, 250);
            _basePruningStrategy.ShouldPrunePersistedNode(state).Returns(true);
            
            // Act
            bool result = _strategy.ShouldPrunePersistedNode(state);

            // Assert
            result.Should().BeTrue();
            _basePruningStrategy.Received(1).ShouldPrunePersistedNode(state);
            
            // Trigger pruning started event
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            
            // Reset the mock to verify the next call
            _basePruningStrategy.ClearReceivedCalls();
            _basePruningStrategy.ShouldPrunePersistedNode(state).Returns(false);
            
            // Act again while in pruning
            result = _strategy.ShouldPrunePersistedNode(state);
            
            // Assert
            result.Should().BeFalse();
            _basePruningStrategy.Received(1).ShouldPrunePersistedNode(state);
        }

        [Test]
        public void PruningStarted_and_PruningFinished_events_should_toggle_pruning_state()
        {
            // Arrange
            _basePruningStrategy.DeleteObsoleteKeys.Returns(true);
            
            // Initial state - not in pruning
            _strategy.DeleteObsoleteKeys.Should().BeTrue();
            
            // Trigger pruning started event
            _fullPruningDb.PruningStarted += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            
            // Should be in pruning state
            _strategy.DeleteObsoleteKeys.Should().BeFalse();
            
            // Trigger pruning finished event
            _fullPruningDb.PruningFinished += Raise.Event<EventHandler<PruningEventArgs>>(null, new PruningEventArgs(Substitute.For<IPruningContext>(), true));
            
            // Should be back to not in pruning state
            _strategy.DeleteObsoleteKeys.Should().BeTrue();
        }
    }
}
