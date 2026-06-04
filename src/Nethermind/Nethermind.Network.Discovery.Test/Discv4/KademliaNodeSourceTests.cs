// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Utils;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
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
            _timestamper = new();
            _timestamper.Set(new DateTimeOffset(2025, 5, 13, 21, 0, 0, TimeSpan.Zero).UtcDateTime);

            _nodeSession = new(_nodeStats, _timestamper);
            _discv4Adapter.GetSession(Arg.Any<Node>()).Returns(_nodeSession);

            _nodeSource = new KademliaNodeSource(
                _kademlia,
                _lookup,
                _discv4Adapter,
                _discoveryConfig,
                LimboLogs.Instance);
        }

        [TearDown]
        public async Task TearDown() => await _discv4Adapter.DisposeAsync();

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_use_lookup_to_find_nodes(CancellationToken token)
        {
            Node node1 = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new(TestItem.PublicKeyB, "192.168.1.2", 30303);
            _nodeSession.OnPongReceived();

            _lookup.Lookup(Arg.Any<PublicKey>(), token)
                .Returns(CreateAsyncEnumerable(node1, node2));
            _discv4Adapter.Ping(node1, token)
                .Returns(true);
            _discv4Adapter.Ping(node2, token)
                .Returns(true);

            IAsyncEnumerator<Node> enumerator = _nodeSource.DiscoverNodes(token).GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();
            Assert.That(enumerator.Current, Is.EqualTo(node1));
            await enumerator.MoveNextAsync();
            Assert.That(enumerator.Current, Is.EqualTo(node2));

            _lookup.Received().Lookup(Arg.Any<PublicKey>(), token);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_ping_nodes_that_have_not_received_pong(CancellationToken token)
        {
            Node node = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            int pingCount = 0;
            _discv4Adapter.Ping(node, token)
                .Returns(true)
                .AndDoes(_ => Interlocked.Increment(ref pingCount));
            _lookup.Lookup(Arg.Any<PublicKey>(), token)
                .Returns(CreateAsyncEnumerable(node));

            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(token);
            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();

            Assert.That(() => Volatile.Read(ref pingCount), Is.GreaterThanOrEqualTo(2).After(5000, 50));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_skip_nodes_that_have_tried_ping_recently_without_pong(CancellationToken token)
        {
            Node node1 = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new(TestItem.PublicKeyB, "192.168.1.2", 30303);

            NodeSession session1 = new(_nodeStats, _timestamper);
            NodeSession session2 = new(_nodeStats, _timestamper);
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
            Assert.That(enumerator.Current, Is.EqualTo(node2));

            await _discv4Adapter.DidNotReceive().Ping(
                Arg.Is<Node>(n => n == node1),
                token);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_handle_ping_timeout(CancellationToken token)
        {
            Node node1 = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new(TestItem.PublicKeyB, "192.168.1.2", 30303);

            int node1PingCount = 0;
            _discv4Adapter.Ping(node1, token)
                .Returns(false)
                .AndDoes(_ => Interlocked.Increment(ref node1PingCount));
            _discv4Adapter.Ping(node2, token)
                .Returns(true);

            _lookup.Lookup(Arg.Any<PublicKey>(), token)
                .Returns(CreateAsyncEnumerable(node1, node2));

            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(token);

            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();
            Assert.That(enumerator.Current, Is.EqualTo(node2));

            Assert.That(() => Volatile.Read(ref node1PingCount), Is.GreaterThanOrEqualTo(2).After(5000, 50));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_emit_nodes_from_kademlia_events(CancellationToken token)
        {
            Node node1 = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new(TestItem.PublicKeyB, "192.168.1.2", 30303);

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

            Assert.That(enumerator.Current, Is.EqualTo(node2));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_not_emit_duplicate_nodes(CancellationToken token)
        {
            Node node = new(TestItem.PublicKeyC, "192.168.1.1", 30303);

            _nodeSession.OnPongReceived();

            _lookup.Lookup(Arg.Any<PublicKey>(), Arg.Any<CancellationToken>())
                .Returns(CreateAsyncEnumerable(node, node));

            using AutoCancelTokenSource shortTimeout = token.CreateChildTokenSource(TimeSpan.FromMilliseconds(100));
            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(shortTimeout.Token);

            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();

            Assert.ThrowsAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync().AsTask());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_use_multiple_concurrent_discovery_jobs(CancellationToken token)
        {
            Node node1 = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new(TestItem.PublicKeyB, "192.168.1.2", 30303);

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
