// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Utils;
using Nethermind.Logging;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4.Kademlia
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NodeSourceTests
    {
        private TestKademlia _kademlia = null!;
        private TestKademliaDiscovery _kademliaDiscovery = null!;
        private IKademliaAdapter _discv4Adapter = null!;
        private NodeSource _nodeSource = null!;
        private NodeSession _nodeSession = null!;
        private INodeStats _nodeStats = null!;
        private ManualTimestamper _timestamper = null!;
        private DiscoveryConfig _discoveryConfig = null!;
        private KademliaConfig<Node> _kademliaConfig = null!;

        [SetUp]
        public void Setup()
        {
            _kademlia = new();
            _kademliaDiscovery = new();
            _discv4Adapter = Substitute.For<IKademliaAdapter>();

            _discoveryConfig = new DiscoveryConfig
            {
                ConcurrentDiscoveryJob = 2
            };
            _kademliaConfig = new()
            {
                CurrentNodeId = new Node(TestItem.PublicKeyD, "127.0.0.1", 30303),
                KSize = 1
            };

            _nodeStats = Substitute.For<INodeStats>();
            _timestamper = new();
            _timestamper.Set(new DateTimeOffset(2025, 5, 13, 21, 0, 0, TimeSpan.Zero).UtcDateTime);

            _nodeSession = new(_nodeStats, _timestamper);
            _discv4Adapter.GetSession(Arg.Any<Node>()).Returns(_nodeSession);

            _nodeSource = new NodeSource(
                _kademlia,
                _kademliaDiscovery,
                _discv4Adapter,
                _discoveryConfig,
                _kademliaConfig,
                LimboLogs.Instance);
        }

        [TearDown]
        public async Task TearDown() => await _discv4Adapter.DisposeAsync();

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_use_kademlia_discovery_to_find_nodes(CancellationToken token)
        {
            Node node1 = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new(TestItem.PublicKeyB, "192.168.1.2", 30303);
            _nodeSession.OnPongReceived(node1.Address);

            _kademliaDiscovery.DiscoverNodesHandler = (_, _, _) => CreateAsyncEnumerable(node1, node2);
            _discv4Adapter.Ping(node1, Arg.Any<CancellationToken>())
                .Returns(true);
            _discv4Adapter.Ping(node2, Arg.Any<CancellationToken>())
                .Returns(true);

            IAsyncEnumerator<Node> enumerator = _nodeSource.DiscoverNodes(token).GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();
            Assert.That(enumerator.Current, Is.EqualTo(node1));
            await enumerator.MoveNextAsync();
            Assert.That(enumerator.Current, Is.EqualTo(node2));

            Assert.That(_kademliaDiscovery.DiscoverNodesCalls, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_ping_nodes_that_have_not_received_pong(CancellationToken token)
        {
            _discoveryConfig.ConcurrentDiscoveryJob = 1;
            Node node = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            _discv4Adapter.Ping(node, Arg.Any<CancellationToken>())
                .Returns(true);
            _kademliaDiscovery.DiscoverNodesHandler = (_, _, _) => CreateAsyncEnumerable(node);

            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(token);
            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();

            // Assert - Verify that ping was called
            await _discv4Adapter.Received(1).Ping(
                Arg.Is<Node>(n => n == node),
                Arg.Any<CancellationToken>());
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
            session2.OnPongReceived(node2.Address);

            _kademliaDiscovery.DiscoverNodesHandler = (_, _, _) => CreateAsyncEnumerable(node1, node2);

            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(token);

            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();
            Assert.That(enumerator.Current, Is.EqualTo(node2));

            await _discv4Adapter.DidNotReceive().Ping(
                Arg.Is<Node>(n => n == node1),
                Arg.Any<CancellationToken>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_handle_ping_timeout(CancellationToken token)
        {
            _discoveryConfig.ConcurrentDiscoveryJob = 1;
            Node node1 = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new(TestItem.PublicKeyB, "192.168.1.2", 30303);

            _discv4Adapter.Ping(node1, Arg.Any<CancellationToken>())
                .Returns(false);
            _discv4Adapter.Ping(node2, Arg.Any<CancellationToken>())
                .Returns(true);

            _kademliaDiscovery.DiscoverNodesHandler = (_, _, _) => CreateAsyncEnumerable(node1, node2);

            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(token);

            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();
            Assert.That(enumerator.Current, Is.EqualTo(node2));

            await _discv4Adapter.Received(1).Ping(
                Arg.Is<Node>(n => n == node1),
                Arg.Any<CancellationToken>());
            await _discv4Adapter.Received(1).Ping(
                Arg.Is<Node>(n => n == node2),
                Arg.Any<CancellationToken>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_emit_nodes_from_kademlia_events(CancellationToken token)
        {
            Node node1 = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new(TestItem.PublicKeyB, "192.168.1.2", 30303);

            _nodeSession.OnPongReceived(node1.Address);

            _kademliaDiscovery.DiscoverNodesHandler = (_, _, _) => CreateAsyncEnumerable(node1);

            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(token);

            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();

            _kademlia.RaiseNodeAdded(node2);

            // Continue iterating
            await enumerator.MoveNextAsync();

            Assert.That(enumerator.Current, Is.EqualTo(node2));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_not_emit_duplicate_nodes(CancellationToken token)
        {
            Node node = new(TestItem.PublicKeyC, "192.168.1.1", 30303);

            _nodeSession.OnPongReceived(node.Address);

            _kademliaDiscovery.DiscoverNodesHandler = (_, _, _) => CreateAsyncEnumerable(node, node);

            using AutoCancelTokenSource shortTimeout = token.CreateChildTokenSource(TimeSpan.FromMilliseconds(100));
            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(shortTimeout.Token);

            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator(token);
            await enumerator.MoveNextAsync();

            Assert.ThrowsAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync().AsTask());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_pass_concurrent_discovery_jobs_to_kademlia_discovery(CancellationToken token)
        {
            Node node1 = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node node2 = new(TestItem.PublicKeyB, "192.168.1.2", 30303);

            _nodeSession.OnPongReceived(node1.Address);

            _kademliaDiscovery.DiscoverNodesHandler = (_, _, _) => CreateAsyncEnumerable(node1, node2);

            IAsyncEnumerable<Node> discoveryEnumerable = _nodeSource.DiscoverNodes(token);

            IAsyncEnumerator<Node> enumerator = discoveryEnumerable.GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
            await enumerator.MoveNextAsync();

            Assert.That(_kademliaDiscovery.ConcurrentDiscoveryJobs, Is.EqualTo(_discoveryConfig.ConcurrentDiscoveryJob));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_stop_background_jobs_when_enumeration_is_disposed(CancellationToken token)
        {
            _discoveryConfig.ConcurrentDiscoveryJob = 1;
            Node node = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            _nodeSession.OnPongReceived(node.Address);
            _kademliaDiscovery.DiscoverNodesHandler = (_, _, _) => CreateAsyncEnumerable(node);

            List<Node> nodes = await _nodeSource.DiscoverNodes(CancellationToken.None).Take(1).ToListAsync(token);

            Assert.That(nodes, Is.EqualTo(new[] { node }));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_release_event_reservation_when_channel_is_full(CancellationToken token)
        {
            _discoveryConfig.ConcurrentDiscoveryJob = 0;
            Node starterNode = CreateNode(1000);
            Node[] queuedNodes = Enumerable.Range(0, 65).Select(CreateNode).ToArray();

            await using IAsyncEnumerator<Node> enumerator = _nodeSource.DiscoverNodes(token).GetAsyncEnumerator(token);
            ValueTask<bool> firstMove = enumerator.MoveNextAsync();
            await Task.Yield();
            _kademlia.RaiseNodeAdded(starterNode);

            Assert.That(await firstMove.AsTask(), Is.True);
            Assert.That(enumerator.Current, Is.EqualTo(starterNode));

            foreach (Node node in queuedNodes)
            {
                _kademlia.RaiseNodeAdded(node);
            }

            for (int i = 0; i < 64; i++)
            {
                Assert.That(await enumerator.MoveNextAsync(), Is.True);
                Assert.That(enumerator.Current, Is.EqualTo(queuedNodes[i]));
            }

            ValueTask<bool> retryMove = enumerator.MoveNextAsync();
            await Task.Yield();
            _kademlia.RaiseNodeAdded(queuedNodes[64]);

            Assert.That(await retryMove.AsTask(), Is.True);
            Assert.That(enumerator.Current, Is.EqualTo(queuedNodes[64]));
        }

        private static async IAsyncEnumerable<T> CreateAsyncEnumerable<T>(params IEnumerable<T> items)
        {
            foreach (T item in items)
            {
                await Task.Yield(); // Add an await to make the method truly async
                yield return item;
            }
        }

        private static Node CreateNode(int index)
        {
            byte[] publicKey = new byte[PublicKey.LengthInBytes];
            publicKey[60] = (byte)(index >> 24);
            publicKey[61] = (byte)(index >> 16);
            publicKey[62] = (byte)(index >> 8);
            publicKey[63] = (byte)index;
            return new Node(new PublicKey(publicKey), $"192.168.{index / 256}.{index % 256}", 30303);
        }

        private sealed class TestKademlia : IKademlia<PublicKey, Node>
        {
            public event EventHandler<Node>? OnNodeAdded;
            public event EventHandler<Node>? OnNodeRemoved { add { } remove { } }

            public void RaiseNodeAdded(Node node) => OnNodeAdded?.Invoke(this, node);

            public void AddOrRefresh(Node node) => throw new NotSupportedException();

            public void Remove(Node node) => throw new NotSupportedException();

            public Task Run(CancellationToken token) => Task.CompletedTask;

            public Task Bootstrap(CancellationToken token) => Task.CompletedTask;

            public Task<Node[]> LookupNodesClosest(PublicKey key, CancellationToken token, int? k = null) =>
                Task.FromResult(Array.Empty<Node>());

            public IAsyncEnumerable<Node> LookupNodes(PublicKey key, CancellationToken token, int? maxResults = null) =>
                throw new NotSupportedException();

            public Node[] GetKNeighbour(PublicKey target, Node? excluding = null, bool excludeSelf = false) => [];

            public Node[] GetAllAtDistance(int distance) => [];

            public IEnumerable<Node> IterateNodes() => [];
        }

        private sealed class TestKademliaDiscovery : IKademliaDiscovery<PublicKey, Node>
        {
            public int DiscoverNodesCalls { get; private set; }

            public int ConcurrentDiscoveryJobs { get; private set; }

            public Func<int, int, CancellationToken, IAsyncEnumerable<Node>> DiscoverNodesHandler { private get; set; } =
                (_, _, _) => CreateAsyncEnumerable<Node>();

            public IAsyncEnumerable<Node> DiscoverNodes(int concurrentDiscoveryJobs, int lookupResultLimit, CancellationToken token)
            {
                DiscoverNodesCalls++;
                ConcurrentDiscoveryJobs = concurrentDiscoveryJobs;
                return DiscoverNodesHandler(concurrentDiscoveryJobs, lookupResultLimit, token);
            }
        }
    }
}
