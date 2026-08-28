// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Stats.Model;
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
            _handler = new(K, IPAddress.Any);
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
            Assert.That(_handler.Handle(msg), Is.False);
            Assert.That(_handler.TaskCompletionSource.Task.IsCompleted, Is.True);
        }

        [Test]
        public async Task When_ResponseContainsUnsupportedFamilies_ThenTheyDoNotConsumeLimit()
        {
            NeighbourMsgHandler handler = new(K, IPAddress.Parse("2001:db8::1"));
            Node[] mixedNodes =
            [
                .. CreateNodes(8),
                .. CreateNodes(8, "2001:db8::")
            ];
            NeighborsMsg mixedMsg = new(_farAddress, _expirationTime, new ArraySegment<Node>(mixedNodes));
            NeighborsMsg ipv6Msg = new(_farAddress, _expirationTime, CreateNodes(8, "2001:db8:1::"));

            Assert.That(handler.Handle(mixedMsg), Is.True);
            Assert.That(handler.TaskCompletionSource.Task.IsCompleted, Is.False);
            Assert.That(handler.Handle(ipv6Msg), Is.True);

            Node[] response = (await handler.TaskCompletionSource.Task).Value;
            Assert.That(response, Has.Length.EqualTo(K));
            Assert.That(Array.TrueForAll(response, node =>
                node.DiscoveryAddress.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6), Is.True);
        }

        private static IEnumerable<TestCaseData> TimeoutCases()
        {
            yield return new TestCaseData(5, new[] { true }).SetName("FewerThanK");
            yield return new TestCaseData(10, new[] { true, false }).SetName("SecondMessageWouldOverflowK");
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
