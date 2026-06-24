// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
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
        private static readonly IPEndPoint TestEndpoint = new(IPAddress.Loopback, 30303);

        [SetUp]
        public void Setup()
        {
            _nodeStats = Substitute.For<INodeStats>();
            _timestamper = new();
            DateTimeOffset currentTime = new(2025, 5, 13, 21, 0, 0, TimeSpan.Zero);
            _timestamper.Set(currentTime.LocalDateTime);
            _nodeSession = new(_nodeStats, _timestamper);
        }

        private static readonly TestCaseData[] FlagTimeoutCases =
        [
            new TestCaseData(
                (Func<NodeSession, bool>)(s => s.HasReceivedPing),
                (Action<NodeSession>)(s => s.OnPingReceived()),
                NodeSession.BondTimeout).SetName(nameof(NodeSession.HasReceivedPing)),
            new TestCaseData(
                (Func<NodeSession, bool>)(s => s.HasReceivedPong),
                (Action<NodeSession>)(s => s.OnPongReceived(TestEndpoint)),
                NodeSession.BondTimeout).SetName(nameof(NodeSession.HasReceivedPong)),
            new TestCaseData(
                (Func<NodeSession, bool>)(s => s.HasTriedPingRecently),
                (Action<NodeSession>)(s => s.OnPingSent()),
                NodeSession.PingRetryTimeout).SetName(nameof(NodeSession.HasTriedPingRecently)),
        ];

        [TestCaseSource(nameof(FlagTimeoutCases))]
        public void Flag_is_set_on_event_and_cleared_after_timeout(
            Func<NodeSession, bool> getter,
            Action<NodeSession> trigger,
            TimeSpan timeout)
        {
            Assert.That(getter(_nodeSession), Is.False);
            trigger(_nodeSession);
            Assert.That(getter(_nodeSession), Is.True);
            _timestamper.Add(timeout);
            Assert.That(getter(_nodeSession), Is.False);
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
