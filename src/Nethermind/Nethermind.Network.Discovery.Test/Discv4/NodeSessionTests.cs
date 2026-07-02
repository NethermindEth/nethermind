// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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
                (Action<NodeSession>)(s => s.OnPingReceived(TestEndpoint)),
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
        public void HasReceivedPingFrom_requires_matching_endpoint()
        {
            IPEndPoint differentEndpoint = new(IPAddress.Parse("192.168.1.1"), TestEndpoint.Port);

            _nodeSession.OnPingReceived(TestEndpoint);

            Assert.That(_nodeSession.HasReceivedPingFrom(TestEndpoint), Is.True);
            Assert.That(_nodeSession.HasReceivedPingFrom(differentEndpoint), Is.False);
        }

        [Test]
        public void HasReceivedPingFrom_keeps_receipts_for_each_endpoint()
        {
            IPEndPoint otherEndpoint = new(IPAddress.Parse("192.168.1.1"), TestEndpoint.Port);

            _nodeSession.OnPingReceived(TestEndpoint);
            _nodeSession.OnPingReceived(otherEndpoint);

            Assert.That(_nodeSession.HasReceivedPingFrom(TestEndpoint), Is.True);
            Assert.That(_nodeSession.HasReceivedPingFrom(otherEndpoint), Is.True);
        }

        [Test]
        public void HasEndpointProof_keeps_proofs_for_each_endpoint()
        {
            IPEndPoint otherEndpoint = new(IPAddress.Parse("192.168.1.1"), TestEndpoint.Port);

            _nodeSession.OnPongReceived(TestEndpoint);
            _nodeSession.OnPongReceived(otherEndpoint);

            Assert.That(_nodeSession.HasEndpointProof(TestEndpoint), Is.True);
            Assert.That(_nodeSession.HasEndpointProof(otherEndpoint), Is.True);
        }

        [Test]
        public void HasReceivedPingFrom_caps_retained_endpoint_receipts()
        {
            const int MaxEndpointReceiptsPerSession = 16;
            IPEndPoint oldestEndpoint = new(IPAddress.Parse("192.168.1.1"), TestEndpoint.Port);
            IPEndPoint newestEndpoint = null!;

            _nodeSession.OnPingReceived(oldestEndpoint);
            for (int i = 0; i < MaxEndpointReceiptsPerSession; i++)
            {
                _timestamper.Add(TimeSpan.FromTicks(1));
                newestEndpoint = new(IPAddress.Parse("192.168.1.1"), TestEndpoint.Port + i + 1);
                _nodeSession.OnPingReceived(newestEndpoint);
            }

            Assert.That(_nodeSession.HasReceivedPingFrom(oldestEndpoint), Is.False);
            Assert.That(_nodeSession.HasReceivedPingFrom(newestEndpoint), Is.True);
        }

        [Test]
        public async Task WaitForEndpointProof_completes_when_matching_pong_is_received()
        {
            _nodeSession.OnPingSent(TestEndpoint);

            Task<bool> waitTask = _nodeSession
                .WaitForEndpointProof(TestEndpoint, TimeSpan.FromSeconds(1), CancellationToken.None)
                .AsTask();

            Assert.That(waitTask.IsCompleted, Is.False);

            _nodeSession.OnPongReceived(TestEndpoint);

            Assert.That(await waitTask, Is.True);
        }

        [Test]
        public async Task WaitForEndpointProof_keeps_pending_proofs_for_each_endpoint()
        {
            IPEndPoint otherEndpoint = new(IPAddress.Parse("192.168.1.1"), TestEndpoint.Port);

            _nodeSession.OnPingSent(TestEndpoint);
            _nodeSession.OnPingSent(otherEndpoint);

            Task<bool> waitTask = _nodeSession
                .WaitForEndpointProof(TestEndpoint, TimeSpan.FromSeconds(1), CancellationToken.None)
                .AsTask();

            Assert.That(waitTask.IsCompleted, Is.False);

            _nodeSession.OnPongReceived(TestEndpoint);

            Assert.That(await waitTask, Is.True);
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
