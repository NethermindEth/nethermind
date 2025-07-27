// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class KademliaNodeSourceTests
    {
        private IKademlia<PublicKey, Node> _kademlia = null!;
        private IIteratorNodeLookup<PublicKey, Node> _lookup = null!;
        private IKademliaDiscv4Adapter _discv4Adapter = null!;
        private KademliaNodeSource _nodeSource = null!;
        private NodeSession _nodeSession = null!;
        private INodeStats _nodeStats = null!;
        private ManualTimestamper _timestamper = null!;
        private DiscoveryConfig _discoveryConfig = null!;

        [SetUp]
        public void Setup()
        {
            _kademlia = Substitute.For<IKademlia<PublicKey, Node>>();
            _lookup = Substitute.For<IIteratorNodeLookup<PublicKey, Node>>();
            _discv4Adapter = Substitute.For<IKademliaDiscv4Adapter>();

            _discoveryConfig = new DiscoveryConfig
            {
                ConcurrentDiscoveryJob = 2
            };

            _nodeStats = Substitute.For<INodeStats>();
            _timestamper = new ManualTimestamper();
            _timestamper.Set(new DateTimeOffset(2025, 5, 13, 21, 0, 0, TimeSpan.Zero).UtcDateTime);

            _nodeSession = new NodeSession(_nodeStats, _timestamper);
            _discv4Adapter.GetSession(Arg.Any<Node>()).Returns(_nodeSession);

            _nodeSource = new KademliaNodeSource(
                _kademlia,
                _lookup,
                _discv4Adapter,
                _discoveryConfig,
                LimboLogs.Instance);
        }

        [TearDown]
        public async Task TearDown()
        {
            await _discv4Adapter.DisposeAsync();
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_use_lookup_to_find_nodes(CancellationToken token)
        {
            Node node1 = new Node(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);
            _nodeSession.OnPongReceived();

            _lookup.Lookup(Arg.Any<PublicKey>(), token)
                .Returns(CreateAsyncEnumerable(node1, node2));
            _discv4Adapter.Ping(node1, token)
                .Returns(Task.CompletedTask);
            _discv4Adapter.Ping(node2, token)
                .Returns(Task.CompletedTask);

            var enumerator = _nodeSource.DiscoverNodes(token).GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync(token);
            enumerator.Current.Should().Be(node1);
            await enumerator.MoveNextAsync(token);
            enumerator.Current.Should().Be(node2);

            _lookup.Received().Lookup(Arg.Any<PublicKey>(), token);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_ping_nodes_that_have_not_received_pong(CancellationToken token)
        {
            Node node = new Node(TestItem.PublicKeyA, "192.168.1.1", 30303);
            _lookup.Lookup(Arg.Any<PublicKey>(), token)
                .Returns(CreateAsyncEnumerable(node));

            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(token);
            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();

            // Assert - Verify that ping was called
            await _discv4Adapter.Received(2).Ping(
                Arg.Is<Node>(n => n == node),
                token);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_skip_nodes_that_have_tried_ping_recently_without_pong(CancellationToken token)
        {
            Node node1 = new Node(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);

            NodeSession session1 = new NodeSession(_nodeStats, _timestamper);
            NodeSession session2 = new NodeSession(_nodeStats, _timestamper);
            _discv4Adapter.GetSession(node1).Returns(session1);
            _discv4Adapter.GetSession(node2).Returns(session2);

            // Set up session1 to have tried ping recently without pong
            session1.OnPingSent();

            // Set up session2 to have received a pong
            session2.OnPongReceived();

            _lookup.Lookup(Arg.Any<PublicKey>(), token)
                .Returns(CreateAsyncEnumerable(node1, node2));

            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(token);

            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();
            enumerator.Current.Should().Be(node2);

            await _discv4Adapter.DidNotReceive().Ping(
                Arg.Is<Node>(n => n == node1),
                token);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_handle_ping_timeout(CancellationToken token)
        {
            Node node1 = new Node(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);

            _discv4Adapter.Ping(node1, token)
                .Returns(Task.FromException(new OperationCanceledException()));
            _discv4Adapter.Ping(node2, token)
                .Returns(Task.CompletedTask);

            _lookup.Lookup(Arg.Any<PublicKey>(), token)
                .Returns(CreateAsyncEnumerable(node1, node2));

            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(token);

            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();
            enumerator.Current.Should().Be(node2);

            await _discv4Adapter.Received(2).Ping(
                Arg.Is<Node>(n => n == node1),
                token);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_emit_nodes_from_kademlia_events(CancellationToken token)
        {
            Node node1 = new Node(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);

            _nodeSession.OnPongReceived();

            _lookup.Lookup(Arg.Any<PublicKey>(), token)
                .Returns(CreateAsyncEnumerable(node1));

            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(token);

            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();

            // Simulate node added event
            _kademlia.OnNodeAdded += Raise.Event<EventHandler<Node>>(null, node2);

            // Continue iterating
            await enumerator.MoveNextAsync();

            enumerator.Current.Should().Be(node2);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_not_emit_duplicate_nodes(CancellationToken token)
        {
            Node node = new Node(TestItem.PublicKeyC, "192.168.1.1", 30303);

            _nodeSession.OnPongReceived();

            _lookup.Lookup(Arg.Any<PublicKey>(), Arg.Any<CancellationToken>())
                .Returns(CreateAsyncEnumerable(node, node));

            using AutoCancelTokenSource shortTimeout = token.CreateChildTokenSource(TimeSpan.FromMilliseconds(100));
            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(shortTimeout.Token);

            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();

            Func<Task<bool>> act = () => enumerator.MoveNextAsync().AsTask();
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_use_multiple_concurrent_discovery_jobs(CancellationToken token)
        {
            Node node1 = new Node(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new Node(TestItem.PublicKeyB, "192.168.1.2", 30303);

            _nodeSession.OnPongReceived();

            // Set up the lookup to return different nodes for different calls
            int callCount = 0;
            _lookup.Lookup(Arg.Any<PublicKey>(), token)
                .Returns(_ =>
                {
                    callCount++;
                    return callCount == 1
                        ? CreateAsyncEnumerable(node1)
                        : CreateAsyncEnumerable(node2);
                });

            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(token);

            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
            await enumerator.MoveNextAsync();

            // Assert - Verify that lookup was called at least twice
            _lookup.Received(2).Lookup(
                Arg.Any<PublicKey>(),
                token);
        }

        private static async IAsyncEnumerable<T> CreateAsyncEnumerable<T>(params IEnumerable<T> items)
        {
            foreach (T item in items)
            {
                await Task.Yield(); // Add an await to make the method truly async
                yield return item;
            }
        }
    }
}
