// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
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
        private static readonly Node InitialNode = new(TestItem.PublicKeyC, "192.168.1.3", 30303);
        private static readonly Node NeighbourNode = new(TestItem.PublicKeyD, "192.168.1.4", 30303);

        private IRoutingTable<Node> _routingTable = null!;
        private IteratorNodeLookup<PublicKey, Node> _lookup = null!;
        private IKademliaMessageSender<PublicKey, Node> _msgSender = null!;
        private Node _currentNode = null!;
        private PublicKey _targetKey = null!;

        [SetUp]
        public void Setup()
        {
            _currentNode = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            _targetKey = TestItem.PublicKeyB;

            _routingTable = Substitute.For<IRoutingTable<Node>>();
            KademliaConfig<Node> kademliaConfig = new() { CurrentNodeId = _currentNode };
            _msgSender = Substitute.For<IKademliaMessageSender<PublicKey, Node>>();
            ILogManager logManager = Substitute.For<ILogManager>();

            _lookup = new IteratorNodeLookup<PublicKey, Node>(
                _routingTable,
                kademliaConfig,
                _msgSender,
                new PublicKeyKeyOperator(),
                new ManualTimestamper(new DateTime(2025, 5, 13, 21, 0, 0, DateTimeKind.Utc)),
                logManager);
        }

        private void RoutingTableReturns(params Node[] nodes) =>
            _routingTable.GetKNearestNeighbour(Arg.Any<ValueHash256>(), Arg.Any<ValueHash256?>())
                .Returns(nodes);

        private void FindNeighboursReturns(Node from, params Node[] result) =>
            _msgSender.FindNeighbours(from, _targetKey, Arg.Any<CancellationToken>())
                .Returns(result);

        private void FindNeighboursThrows(Node from, Exception exception) =>
            _msgSender.FindNeighbours(from, _targetKey, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<Node[]?>(exception));

        private Task AssertFindNeighboursCalledOnce(Node node) =>
            _msgSender.Received(1).FindNeighbours(
                Arg.Is<Node>(n => n == node),
                Arg.Is<PublicKey>(k => k == _targetKey),
                Arg.Any<CancellationToken>());

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_return_nodes_from_routing_table(CancellationToken token)
        {
            Node[] expectedNodes = [InitialNode, NeighbourNode];
            RoutingTableReturns(expectedNodes);

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync(token);

            Assert.That(result, Is.EquivalentTo(expectedNodes));
            _routingTable.Received(1).GetKNearestNeighbour(
                Arg.Is<ValueHash256>(h => h == _targetKey.Hash),
                Arg.Any<ValueHash256?>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_query_nodes_and_return_neighbours(CancellationToken token)
        {
            RoutingTableReturns(InitialNode);
            FindNeighboursReturns(InitialNode, NeighbourNode);

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync(token);

            Assert.That(result, Is.EquivalentTo(new[] { InitialNode, NeighbourNode }));
            await AssertFindNeighboursCalledOnce(InitialNode);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_not_query_self_node(CancellationToken token)
        {
            RoutingTableReturns(_currentNode);

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync(token);

            Assert.That(result, Is.EquivalentTo(new[] { _currentNode }));

            await _msgSender.DidNotReceive().FindNeighbours(
                Arg.Any<Node>(),
                Arg.Any<PublicKey>(),
                Arg.Any<CancellationToken>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_handle_empty_neighbour_response(CancellationToken token)
        {
            RoutingTableReturns(InitialNode);
            FindNeighboursReturns(InitialNode);

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync(token);

            Assert.That(result, Is.EquivalentTo(new[] { InitialNode }));
            await AssertFindNeighboursCalledOnce(InitialNode);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_handle_exception_in_find_neighbours(CancellationToken token)
        {
            RoutingTableReturns(InitialNode);
            FindNeighboursThrows(InitialNode, new Exception("Test exception"));

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync(token);

            Assert.That(result, Is.EquivalentTo(new[] { InitialNode }));
            await AssertFindNeighboursCalledOnce(InitialNode);
        }

        [Test]
        [CancelAfter(10000)]
        public void Lookup_should_respect_cancellation_token(CancellationToken token)
        {
            RoutingTableReturns(InitialNode);

            using CancellationTokenSource cts = new();
            cts.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(async () => await _lookup.Lookup(_targetKey, cts.Token).ToListAsync());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_not_query_same_node_twice(CancellationToken token)
        {
            RoutingTableReturns(InitialNode);
            FindNeighboursReturns(InitialNode, NeighbourNode);
            FindNeighboursReturns(NeighbourNode, InitialNode);

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync();

            Assert.That(result, Is.EquivalentTo(new[] { InitialNode, NeighbourNode }));
            await AssertFindNeighboursCalledOnce(InitialNode);
            await AssertFindNeighboursCalledOnce(NeighbourNode);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Lookup_should_not_return_duplicate_nodes(CancellationToken token)
        {
            RoutingTableReturns(InitialNode);
            FindNeighboursReturns(InitialNode, NeighbourNode);
            FindNeighboursReturns(NeighbourNode, InitialNode, NeighbourNode);

            List<Node> result = await _lookup.Lookup(_targetKey, token).ToListAsync();

            Assert.That(result, Is.EquivalentTo(new[] { InitialNode, NeighbourNode }));
        }
    }
}
