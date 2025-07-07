// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NodeSessionTests
    {
        private INodeStats _nodeStats = null!;
        private ManualTimestamper _timestamper = null!;
        private NodeSession _nodeSession = null!;

        [SetUp]
        public void Setup()
        {
            _nodeStats = Substitute.For<INodeStats>();
            _timestamper = new ManualTimestamper();
            DateTimeOffset currentTime = new DateTimeOffset(2025, 5, 13, 21, 0, 0, TimeSpan.Zero);
            _timestamper.Set(currentTime.LocalDateTime);
            _nodeSession = new NodeSession(_nodeStats, _timestamper);
        }

        [Test]
        public void Test_HasReceivedPing()
        {
            _nodeSession.HasReceivedPing.Should().BeFalse();
            _nodeSession.OnPingReceived();
            _nodeSession.HasReceivedPing.Should().BeTrue();
            _timestamper.Add(NodeSession.BondTimeout);
            _nodeSession.HasReceivedPing.Should().BeFalse();
        }

        [Test]
        public void Test_HasReceivedPong()
        {
            _nodeSession.HasReceivedPong.Should().BeFalse();
            _nodeSession.OnPongReceived();
            _nodeSession.HasReceivedPong.Should().BeTrue();
            _timestamper.Add(NodeSession.BondTimeout);
            _nodeSession.HasReceivedPong.Should().BeFalse();
        }

        [Test]
        public void Test_HasTriedPingRecently()
        {
            _nodeSession.HasTriedPingRecently.Should().BeFalse();
            _nodeSession.OnPingSent();
            _nodeSession.HasTriedPingRecently.Should().BeTrue();
            _timestamper.Add(NodeSession.PingRetryTimeout);
            _nodeSession.HasTriedPingRecently.Should().BeFalse();
        }

        [Test]
        public void Test_NotTooManyFailures()
        {
            _nodeSession.NotTooManyFailure.Should().BeTrue();
            _nodeSession.OnAuthenticatedRequestFailure();
            _nodeSession.NotTooManyFailure.Should().BeTrue();

            for (int i = 0; i < NodeSession.AuthenticatedRequestFailureLimit; i++)
            {
                _nodeSession.OnAuthenticatedRequestFailure();
            }
            _nodeSession.NotTooManyFailure.Should().BeFalse();
            _nodeSession.ResetAuthenticatedRequestFailure();
            _nodeSession.NotTooManyFailure.Should().BeTrue();
        }
    }
}
