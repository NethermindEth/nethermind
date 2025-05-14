// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NodeSessionTests
    {
        private INodeStats _nodeStats = null!;
        private ITimestamper _timestamper = null!;
        private NodeSession _nodeSession = null!;
        private DateTimeOffset _currentTime;

        [SetUp]
        public void Setup()
        {
            _currentTime = new DateTimeOffset(2025, 5, 13, 21, 0, 0, TimeSpan.Zero);
            _nodeStats = Substitute.For<INodeStats>();
            _timestamper = Substitute.For<ITimestamper>();
            _timestamper.UtcNowOffset.Returns(_currentTime);
            _nodeSession = new NodeSession(_nodeStats, _timestamper);
        }

        [Test]
        public void HasReceivedPing_should_return_false_when_no_ping_received()
        {
            // Act
            bool result = _nodeSession.HasReceivedPing;

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void HasReceivedPing_should_return_true_when_ping_received_within_bond_timeout()
        {
            // Arrange
            _nodeSession.OnPingReceived();

            // Act
            bool result = _nodeSession.HasReceivedPing;

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void HasReceivedPing_should_return_false_when_ping_received_outside_bond_timeout()
        {
            // Arrange
            _nodeSession.OnPingReceived();
            
            // Move time forward by more than the bond timeout (12 hours)
            _timestamper.UtcNowOffset.Returns(_currentTime.AddHours(13));

            // Act
            bool result = _nodeSession.HasReceivedPing;

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void HasReceivedPong_should_return_false_when_no_pong_received()
        {
            // Act
            bool result = _nodeSession.HasReceivedPong;

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void HasReceivedPong_should_return_true_when_pong_received_within_bond_timeout()
        {
            // Arrange
            _nodeSession.OnPongReceived();

            // Act
            bool result = _nodeSession.HasReceivedPong;

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void HasReceivedPong_should_return_false_when_pong_received_outside_bond_timeout()
        {
            // Arrange
            _nodeSession.OnPongReceived();
            
            // Move time forward by more than the bond timeout (12 hours)
            _timestamper.UtcNowOffset.Returns(_currentTime.AddHours(13));

            // Act
            bool result = _nodeSession.HasReceivedPong;

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void HasTriedPingRecently_should_return_false_when_no_ping_sent()
        {
            // Act
            bool result = _nodeSession.HasTriedPingRecently;

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void HasTriedPingRecently_should_return_true_when_ping_sent_within_10_minutes()
        {
            // Arrange
            _nodeSession.OnPingSent();

            // Act
            bool result = _nodeSession.HasTriedPingRecently;

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void HasTriedPingRecently_should_return_false_when_ping_sent_more_than_10_minutes_ago()
        {
            // Arrange
            _nodeSession.OnPingSent();
            
            // Move time forward by more than 10 minutes
            _timestamper.UtcNowOffset.Returns(_currentTime.AddMinutes(11));

            // Act
            bool result = _nodeSession.HasTriedPingRecently;

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void OnPongReceived_should_update_LastPongReceived()
        {
            // Arrange
            _nodeSession.HasReceivedPong.Should().BeFalse();

            // Act
            _nodeSession.OnPongReceived();

            // Assert
            _nodeSession.HasReceivedPong.Should().BeTrue();
        }

        [Test]
        public void OnPingReceived_should_update_LastPingReceived()
        {
            // Arrange
            _nodeSession.HasReceivedPing.Should().BeFalse();

            // Act
            _nodeSession.OnPingReceived();

            // Assert
            _nodeSession.HasReceivedPing.Should().BeTrue();
        }

        [Test]
        public void OnPingSent_should_update_LastPingSent()
        {
            // Arrange
            _nodeSession.HasTriedPingRecently.Should().BeFalse();

            // Act
            _nodeSession.OnPingSent();

            // Assert
            _nodeSession.HasTriedPingRecently.Should().BeTrue();
        }

        [Test]
        public void NotTooManyFailure_should_return_true_initially()
        {
            // Act
            bool result = _nodeSession.NotTooManyFailure;

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void NotTooManyFailure_should_return_false_after_too_many_failures()
        {
            // Arrange
            for (int i = 0; i < 6; i++) // AuthenticatedRequestFailureLimit is 5
            {
                _nodeSession.OnAuthenticatedRequestFailure();
            }

            // Act
            bool result = _nodeSession.NotTooManyFailure;

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void ResetAuthenticatedRequestFailure_should_reset_failure_count()
        {
            // Arrange
            for (int i = 0; i < 6; i++)
            {
                _nodeSession.OnAuthenticatedRequestFailure();
            }
            _nodeSession.NotTooManyFailure.Should().BeFalse();

            // Act
            _nodeSession.ResetAuthenticatedRequestFailure();

            // Assert
            _nodeSession.NotTooManyFailure.Should().BeTrue();
        }
    }
}
