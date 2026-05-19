// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            _timestamper = new();
            DateTimeOffset currentTime = new(2025, 5, 13, 21, 0, 0, TimeSpan.Zero);
            _timestamper.Set(currentTime.LocalDateTime);
            _nodeSession = new(_nodeStats, _timestamper);
        }

        [Test]
        public void Test_HasReceivedPing()
        {
            Assert.That(_nodeSession.HasReceivedPing, Is.False);
            _nodeSession.OnPingReceived();
            Assert.That(_nodeSession.HasReceivedPing, Is.True);
            _timestamper.Add(NodeSession.BondTimeout);
            Assert.That(_nodeSession.HasReceivedPing, Is.False);
        }

        [Test]
        public void Test_HasReceivedPong()
        {
            Assert.That(_nodeSession.HasReceivedPong, Is.False);
            _nodeSession.OnPongReceived();
            Assert.That(_nodeSession.HasReceivedPong, Is.True);
            _timestamper.Add(NodeSession.BondTimeout);
            Assert.That(_nodeSession.HasReceivedPong, Is.False);
        }

        [Test]
        public void Test_HasTriedPingRecently()
        {
            Assert.That(_nodeSession.HasTriedPingRecently, Is.False);
            _nodeSession.OnPingSent();
            Assert.That(_nodeSession.HasTriedPingRecently, Is.True);
            _timestamper.Add(NodeSession.PingRetryTimeout);
            Assert.That(_nodeSession.HasTriedPingRecently, Is.False);
        }

        [Test]
        public void Test_NotTooManyFailures()
        {
            Assert.That(_nodeSession.NotTooManyFailure, Is.True);
            _nodeSession.OnAuthenticatedRequestFailure();
            Assert.That(_nodeSession.NotTooManyFailure, Is.True);

            for (int i = 0; i < NodeSession.AuthenticatedRequestFailureLimit; i++)
            {
                _nodeSession.OnAuthenticatedRequestFailure();
            }
            Assert.That(_nodeSession.NotTooManyFailure, Is.False);
            _nodeSession.ResetAuthenticatedRequestFailure();
            Assert.That(_nodeSession.NotTooManyFailure, Is.True);
        }
    }
}
