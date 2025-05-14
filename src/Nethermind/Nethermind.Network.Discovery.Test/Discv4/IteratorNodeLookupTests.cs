// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class IteratorNodeLookupTests
    {
        private IRoutingTable<Node> _routingTable = null!;
        private IKademliaDiscv4Adapter _discv4Adapter = null!;
        private IteratorNodeLookup _lookup = null!;
        private Node _currentNode = null!;
        private PublicKey _targetKey = null!;

        [SetUp]
        public void Setup()
        {
            _currentNode = new Node(TestItem.PublicKeyA, "192.168.1.1", 30303);
            _targetKey = TestItem.PublicKeyB;

            _routingTable = Substitute.For<IRoutingTable<Node>>();
            KademliaConfig<Node> kademliaConfig = new KademliaConfig<Node> { CurrentNodeId = _currentNode };
            _discv4Adapter = Substitute.For<IKademliaDiscv4Adapter>();
            ILogManager logManager = Substitute.For<ILogManager>();

            _lookup = new IteratorNodeLookup(_routingTable, kademliaConfig, _discv4Adapter, logManager);
        }

        [TearDown]
        public async Task TearDown()
        {
            await _discv4Adapter.DisposeAsync();
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_return_nodes_from_routing_table(CancellationToken token)
        {
            Node[] expectedNodes =
            [
                new(TestItem.PublicKeyC, "192.168.1.3", 30303),
                new(TestItem.PublicKeyD, "192.168.1.4", 30303)
            ];

            _routingTable.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256?>())
                .Returns(expectedNodes);

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync(token);

            result.Should().BeEquivalentTo(expectedNodes);
            _routingTable.Received(1).GetKNearestNeighbour(
                Arg.Is<ValueHash256>(h => h == _targetKey.Hash),
                Arg.Any<ValueHash256?>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_query_nodes_and_return_neighbours(CancellationToken token)
        {
            Node initialNode = new Node(TestItem.PublicKeyC, "192.168.1.3", 30303);
            Node neighbourNode = new Node(TestItem.PublicKeyD, "192.168.1.4", 30303);

            _routingTable.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256?>())
                .Returns([initialNode]);

            _discv4Adapter.FindNeighbours(initialNode, _targetKey, Arg.Any<CancellationToken>())
                .Returns([neighbourNode]);

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync(token);

            result.Should().HaveCount(2);
            result.Should().Contain(initialNode);
            result.Should().Contain(neighbourNode);

            await _discv4Adapter.Received(1).FindNeighbours(
                Arg.Is<Node>(n => n == initialNode),
                Arg.Is<PublicKey>(k => k == _targetKey),
                Arg.Any<CancellationToken>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_not_query_self_node(CancellationToken token)
        {
            _routingTable.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256?>())
                .Returns([_currentNode]);

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync(token);

            result.Should().HaveCount(1);
            result.Should().Contain(_currentNode);

            await _discv4Adapter.DidNotReceive().FindNeighbours(
                Arg.Any<Node>(),
                Arg.Any<PublicKey>(),
                Arg.Any<CancellationToken>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_handle_empty_neighbour_response(CancellationToken token)
        {
            Node initialNode = new Node(TestItem.PublicKeyC, "192.168.1.3", 30303);

            _routingTable.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256?>())
                .Returns([initialNode]);

            _discv4Adapter.FindNeighbours(initialNode, _targetKey, Arg.Any<CancellationToken>())
                .Returns([]);

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync(token);

            result.Should().HaveCount(1);
            result.Should().Contain(initialNode);

            await _discv4Adapter.Received(1).FindNeighbours(
                Arg.Is<Node>(n => n == initialNode),
                Arg.Is<PublicKey>(k => k == _targetKey),
                Arg.Any<CancellationToken>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_handle_exception_in_find_neighbours(CancellationToken token)
        {
            Node initialNode = new Node(TestItem.PublicKeyC, "192.168.1.3", 30303);

            _routingTable.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256?>())
                .Returns(new[] { initialNode });

            _discv4Adapter.FindNeighbours(initialNode, _targetKey, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<Node[]>(new Exception("Test exception")));

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync(token);

            result.Should().HaveCount(1);
            result.Should().Contain(initialNode);

            await _discv4Adapter.Received(1).FindNeighbours(
                Arg.Is<Node>(n => n == initialNode),
                Arg.Is<PublicKey>(k => k == _targetKey),
                Arg.Any<CancellationToken>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_respect_cancellation_token(CancellationToken token)
        {
            Node initialNode = new Node(TestItem.PublicKeyC, "192.168.1.3", 30303);

            _routingTable.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256?>())
                .Returns([initialNode]);

            using CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> act = async () => await _lookup.Lookup(_targetKey, cts.Token).ToListAsync();
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_not_query_same_node_twice(CancellationToken token)
        {
            Node initialNode = new Node(TestItem.PublicKeyC, "192.168.1.3", 30303);
            Node neighbourNode = new Node(TestItem.PublicKeyD, "192.168.1.4", 30303);

            _routingTable.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256?>())
                .Returns([initialNode]);

            _discv4Adapter.FindNeighbours(initialNode, _targetKey, Arg.Any<CancellationToken>())
                .Returns([neighbourNode]);

            _discv4Adapter.FindNeighbours(neighbourNode, _targetKey, Arg.Any<CancellationToken>())
                .Returns([initialNode]);

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync();

            result.Should().HaveCount(2);
            result.Should().Contain(initialNode);
            result.Should().Contain(neighbourNode);

            await _discv4Adapter.Received(1).FindNeighbours(
                Arg.Is<Node>(n => n == initialNode),
                Arg.Is<PublicKey>(k => k == _targetKey),
                Arg.Any<CancellationToken>());

            await _discv4Adapter.Received(1).FindNeighbours(
                Arg.Is<Node>(n => n == neighbourNode),
                Arg.Is<PublicKey>(k => k == _targetKey),
                Arg.Any<CancellationToken>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_not_return_duplicate_nodes(CancellationToken token)
        {
            Node initialNode = new Node(TestItem.PublicKeyC, "192.168.1.3", 30303);
            Node neighbourNode = new Node(TestItem.PublicKeyD, "192.168.1.4", 30303);

            _routingTable.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256?>())
                .Returns([initialNode]);

            _discv4Adapter.FindNeighbours(initialNode, _targetKey, Arg.Any<CancellationToken>())
                .Returns([neighbourNode]);

            _discv4Adapter.FindNeighbours(neighbourNode, _targetKey, Arg.Any<CancellationToken>())
                .Returns([initialNode, neighbourNode]);

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync();

            result.Should().HaveCount(2);
            result.Should().Contain(initialNode);
            result.Should().Contain(neighbourNode);
        }
    }
}
