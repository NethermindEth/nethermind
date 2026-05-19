// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4
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
            _handler = new(K);
            _farAddress = new(IPAddress.Parse("192.168.1.1"), 30303);
            _expirationTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60; // 60 seconds in the future
        }

        [Test]
        public async Task When_TotalNodesLessThanK_ThenDontFinish_UntilTimeout()
        {
            ArraySegment<Node> nodes = CreateNodes(5);
            NeighborsMsg msg = new(_farAddress, _expirationTime, nodes);

            Assert.That(_handler.Handle(msg), Is.True);
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
        public async Task When_TotalNodesDoesNotAddUp_DontTakeMessage()
        {
            ArraySegment<Node> nodes = CreateNodes(10);
            NeighborsMsg msg = new(_farAddress, _expirationTime, nodes);

            Assert.That(_handler.Handle(msg), Is.True);
            Assert.That(_handler.Handle(msg), Is.False);
            Assert.That(_handler.TaskCompletionSource.Task.IsCompleted, Is.False);
            await _handler.TaskCompletionSource.Task;
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
    }
}
