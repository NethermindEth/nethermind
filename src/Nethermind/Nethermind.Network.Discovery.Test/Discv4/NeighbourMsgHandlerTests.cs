// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
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
            _handler = new NeighbourMsgHandler(K);
            _farAddress = new IPEndPoint(IPAddress.Parse("192.168.1.1"), 30303);
            _expirationTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60; // 60 seconds in the future
        }

        [Test]
        public async Task When_TotalNodesLessThanK_ThenDontFinish_UntilTimeout()
        {
            var nodes = CreateNodes(5);
            var msg = new NeighborsMsg(_farAddress, _expirationTime, nodes);

            _handler.Handle(msg).Should().BeTrue();
            _handler.TaskCompletionSource.Task.IsCompleted.Should().BeFalse();

            await _handler.TaskCompletionSource.Task;
        }

        [Test]
        public void When_TotalNodesLessEqualToK_ThenFinishImmediately()
        {
            var nodes = CreateNodes(8);
            var msg = new NeighborsMsg(_farAddress, _expirationTime, nodes);

            _handler.Handle(msg).Should().BeTrue();
            _handler.Handle(msg).Should().BeTrue();
            _handler.Handle(msg).Should().BeFalse();
            _handler.TaskCompletionSource.Task.IsCompleted.Should().BeTrue();
        }

        [Test]
        public async Task When_TotalNodesDoesNotAddUp_DontTakeMessage()
        {
            var nodes = CreateNodes(10);
            var msg = new NeighborsMsg(_farAddress, _expirationTime, nodes);

            _handler.Handle(msg).Should().BeTrue();
            _handler.Handle(msg).Should().BeFalse();
            _handler.TaskCompletionSource.Task.IsCompleted.Should().BeFalse();
            await _handler.TaskCompletionSource.Task;
        }

        private ArraySegment<Node> CreateNodes(int count, int startIndex = 0)
        {
            var nodes = new Node[count];
            for (int i = 0; i < count; i++)
            {
                var publicKey = TestItem.PublicKeys[i];
                nodes[i] = new Node(publicKey, $"192.168.1.{i + startIndex + 10}", 30303);
            }
            return new ArraySegment<Node>(nodes);
        }
    }
}
