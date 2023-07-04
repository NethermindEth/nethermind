// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test
{
    [TestFixture]
    public class NodesLocatorTests
    {
        private NodesLocator? _nodesLocator;
        private NodeTable? _nodeTable;
        private Node? _masterNode;

        [SetUp]
        public void Setup()
        {
            NetworkConfig networkConfig = new();
            networkConfig.ExternalIp = IPAddress.Broadcast.ToString();

            _masterNode = new Node(TestItem.PublicKeyA, IPAddress.Broadcast.ToString(), 30000);
            DiscoveryConfig config = new() { DiscoveryNewCycleWaitTime = 1 };
            NodeDistanceCalculator distanceCalculator = new(config);
            _nodeTable = new NodeTable(distanceCalculator, config, networkConfig, LimboLogs.Instance);
            EvictionManager evictionManager = new(_nodeTable, LimboLogs.Instance);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            NodeStatsManager nodeStatsManager = new(timerFactory, LimboLogs.Instance);
            NodeLifecycleManagerFactory managerFactory =
                new(
                    _nodeTable,
                    evictionManager,
                    nodeStatsManager,
                    new NodeRecord(),
                    config,
                    Timestamper.Default,
                    LimboLogs.Instance);
            DiscoveryManager manager = new(
                managerFactory,
                _nodeTable,
                new NetworkStorage(new MemDb(), LimboLogs.Instance),
                config,
                LimboLogs.Instance);
            _nodesLocator = new NodesLocator(_nodeTable, manager, config, LimboLogs.Instance);
        }

        [Test]
        public async Task Can_locate_nodes_when_no_nodes()
        {
            _nodesLocator!.Initialize(_masterNode!);
            _nodeTable!.Initialize(_masterNode!.Id);
            await _nodesLocator.LocateNodesAsync(CancellationToken.None);
        }

        [TestCase(1)]
        [TestCase(256)]
        [TestCase(1024)]
        public async Task Can_locate_nodes_when_some_nodes(int nodesCount)
        {
            _nodesLocator!.Initialize(_masterNode!);
            _nodeTable!.Initialize(_masterNode!.Id);

            for (int i = 0; i < nodesCount; i++)
            {
                Node node = new(TestItem.PublicKeyA, IPAddress.Broadcast.ToString(), 30000 + i);
                _nodeTable.AddNode(node);
            }

            await _nodesLocator.LocateNodesAsync(CancellationToken.None);
        }

        [Test]
        public void Throws_when_uninitialized()
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => _nodesLocator!.LocateNodesAsync(CancellationToken.None));
        }
    }
}
