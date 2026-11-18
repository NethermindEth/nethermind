// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4.Kademlia.Handlers
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NeighbourMsgHandlerTests
    {
        private NeighbourMsgHandler _handler;
        private const int K = 16;
        private IPEndPoint _farAddress;
        private long _expirationTime;

        [SetUp]
        public void Setup()
        {
            _handler = new(K, IPAddress.Parse("192.168.1.2"), PeerAddressPermissions.Private);
            _farAddress = new(IPAddress.Parse("192.168.1.1"), 30303);
            _expirationTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60; // 60 seconds in the future
        }

        [TestCaseSource(nameof(TimeoutCases))]
        public async Task When_TotalNodesDoNotCompleteImmediately_ThenCompleteAfterTimeout(int nodeCount, bool[] expectedHandleResults)
        {
            ArraySegment<Node> nodes = CreateNodes(nodeCount);
            NeighborsMsg msg = new(_farAddress, _expirationTime, nodes);

            for (int i = 0; i < expectedHandleResults.Length; i++)
            {
                Assert.That(_handler.Handle(msg), Is.EqualTo(expectedHandleResults[i]));
            }

            Assert.That(_handler.TaskCompletionSource.Task.IsCompleted, Is.False);

            await _handler.TaskCompletionSource.Task;
        }

        [Test]
        public void When_TotalNodesLessEqualToK_ThenFinishImmediately()
        {
            ArraySegment<Node> nodes = CreateNodes(8);
            NeighborsMsg msg = new(_farAddress, _expirationTime, nodes);

            Assert.That(_handler.Handle(msg), Is.True);
            Assert.That(_handler.Handle(msg), Is.True);
#pragma warning disable NUnit2045 // Message handling changes completion state used by subsequent assertions or messages.
            Assert.That(_handler.Handle(msg), Is.False);
#pragma warning restore NUnit2045
            Assert.That(_handler.TaskCompletionSource.Task.IsCompleted, Is.True);
        }

        [Test]
        public async Task When_ResponseWouldOverflowK_ThenCompletesWithFirstKNodes()
        {
            NeighborsMsg first = new(_farAddress, _expirationTime, CreateNodes(10));
            NeighborsMsg second = new(_farAddress, _expirationTime, CreateNodes(10, startIndex: 10));

#pragma warning disable NUnit2045 // Message handling changes completion state used by subsequent assertions or messages.
            Assert.That(_handler.Handle(first), Is.True);
#pragma warning restore NUnit2045
            Assert.That(_handler.Handle(second), Is.True);
            Assert.That(_handler.TaskCompletionSource.Task.IsCompleted, Is.True);

            Node[] response = (await _handler.TaskCompletionSource.Task).Value;
            Assert.That(response, Has.Length.EqualTo(K));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(response[9].Host, Is.EqualTo("192.168.1.19"));
                Assert.That(response[10].Host, Is.EqualTo("192.168.1.20"));
                Assert.That(response[15].Host, Is.EqualTo("192.168.1.25"));
            }
        }

        [Test]
        public async Task When_ResponseContainsUnsupportedFamilies_ThenTheyDoNotConsumeLimit()
        {
            NeighbourMsgHandler handler = new(K, IPAddress.Parse("2001:4860::1"), PeerAddressPermissions.None);
            Node[] mixedNodes =
            [
                .. CreateNodes(8),
                .. CreateNodes(8, "2001:4860::")
            ];
            NeighborsMsg mixedMsg = new(_farAddress, _expirationTime, new ArraySegment<Node>(mixedNodes));
            NeighborsMsg ipv6Msg = new(_farAddress, _expirationTime, CreateNodes(8, "2001:4860:1::"));

#pragma warning disable NUnit2045 // Message handling changes completion state used by subsequent assertions or messages.
            Assert.That(handler.Handle(mixedMsg), Is.True);
#pragma warning restore NUnit2045
            Assert.That(handler.TaskCompletionSource.Task.IsCompleted, Is.False);
            Assert.That(handler.Handle(ipv6Msg), Is.True);

            Node[] response = (await handler.TaskCompletionSource.Task).Value;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(response, Has.Length.EqualTo(K));
                Assert.That(Array.TrueForAll(response, node =>
                    node.DiscoveryAddress.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6), Is.True);
            }
        }

        [Test]
        public async Task Rejected_relay_does_not_consume_result_quota(
            [Values("0.0.0.0", "127.0.0.1", "10.0.0.1", "169.254.1.1", "224.0.0.1", "::1", "fe80::1", "fc00::1", "::ffff:127.0.0.1")] string address)
        {
            IIPResolver resolver = Substitute.For<IIPResolver>();
            resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
                new IIPResolver.NethermindIp(IPAddress.IPv6Any, IPAddress.None)));
            PeerAddressPolicy policy = new(resolver, new LocalAddressSource(LimboLogs.Instance,
                () => [IPAddress.Loopback, IPAddress.Parse("169.254.1.2"), IPAddress.Parse("fe80::1"), IPAddress.Parse("8.8.8.8")]));
            NeighbourMsgHandler handler = new(1, IPAddress.IPv6Any, policy.AllowedAddresses);
            Node rejected = new(TestItem.PublicKeyA, address, 30303);
            Node accepted = new(TestItem.PublicKeyB, "8.8.8.8", 30303);
            handler.Handle(new NeighborsMsg(_farAddress, _expirationTime, new ArraySegment<Node>([rejected, accepted])));

            Node[] nodes = (await handler.TaskCompletionSource.Task).Value;
            Assert.That(nodes, Is.EqualTo(new[] { accepted }));
        }

        [Test]
        public async Task Relayed_self_endpoint_does_not_consume_result_quota()
        {
            Node self = new(TestItem.PublicKeyA, "8.8.8.8", 30303);
            NeighbourMsgHandler handler = new(1, IPAddress.IPv6Any, PeerAddressPermissions.None) { LocalNode = self };
            Node rejected = new(TestItem.PublicKeyB, "::ffff:8.8.8.8", 30303);
            Node accepted = new(TestItem.PublicKeyC, "8.8.4.4", 30303);
            handler.Handle(new NeighborsMsg(_farAddress, _expirationTime, new ArraySegment<Node>([rejected, accepted])));
            Assert.That((await handler.TaskCompletionSource.Task).Value, Is.EqualTo(new[] { accepted }));
        }

        private static IEnumerable<TestCaseData> TimeoutCases()
        {
            yield return new TestCaseData(5, new[] { true }).SetName("FewerThanK");
        }

        private ArraySegment<Node> CreateNodes(int count, int startIndex = 0)
        {
            Node[] nodes = new Node[count];
            for (int i = 0; i < count; i++)
            {
                PublicKey publicKey = TestItem.PublicKeys[i];
                nodes[i] = new(publicKey, $"192.168.1.{i + startIndex + 10}", 30303);
            }
            return new(nodes);
        }

        private ArraySegment<Node> CreateNodes(int count, string prefix)
        {
            Node[] nodes = new Node[count];
            for (int i = 0; i < count; i++)
            {
                nodes[i] = new Node(TestItem.PublicKeys[i], $"{prefix}{i + 10}", 30303);
            }

            return new(nodes);
        }
    }
}
