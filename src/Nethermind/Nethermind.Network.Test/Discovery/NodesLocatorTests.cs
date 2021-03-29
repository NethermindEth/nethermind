//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [TestFixture]
    public class NodesLocatorTests
    {
        private NodesLocator _nodesLocator;
        private NodeTable _nodeTable;
        private Node _masterNode;

        [SetUp]
        public void Setup()
        {
            NetworkConfig networkConfig = new NetworkConfig();
            networkConfig.ExternalIp = IPAddress.Broadcast.ToString();
            
            _masterNode = new Node(TestItem.PublicKeyA, IPAddress.Broadcast.ToString(), 30000, false);
            DiscoveryConfig config = new DiscoveryConfig() {DiscoveryNewCycleWaitTime = 1};
            NodeDistanceCalculator distanceCalculator = new NodeDistanceCalculator(config);
            _nodeTable = new NodeTable(
                distanceCalculator,
                config,
                networkConfig,
                LimboLogs.Instance);
            DiscoveryMessageFactory messageFactory = new DiscoveryMessageFactory(Timestamper.Default);
            EvictionManager evictionManager = new EvictionManager(_nodeTable, LimboLogs.Instance);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            NodeStatsManager nodeStatsManager = new NodeStatsManager(timerFactory, LimboLogs.Instance);
            NodeLifecycleManagerFactory managerFactory =
                new NodeLifecycleManagerFactory(
                    _nodeTable,
                    messageFactory,
                    evictionManager,
                    nodeStatsManager,
                    config,
                    LimboLogs.Instance);
            DiscoveryManager manager = new DiscoveryManager(
                managerFactory,
                _nodeTable,
                new NetworkStorage(new MemDb(), LimboLogs.Instance),
                config,
                LimboLogs.Instance, 
                new IPResolver(networkConfig, LimboLogs.Instance));
            _nodesLocator = new NodesLocator(_nodeTable, manager, config, LimboLogs.Instance);   
        }
        
        [Test]
        public async Task Can_locate_nodes_when_no_nodes()
        {
            _nodesLocator.Initialize(_masterNode);
            _nodeTable.Initialize(_masterNode.Id);
            await _nodesLocator.LocateNodesAsync(CancellationToken.None);
        }
        
        [TestCase(1)]
        [TestCase(256)]
        [TestCase(1024)]
        public async Task Can_locate_nodes_when_some_nodes(int nodesCount)
        {
            _nodesLocator.Initialize(_masterNode);
            _nodeTable.Initialize(_masterNode.Id);

            for (int i = 0; i < nodesCount; i++)
            {
                Node node = new Node(TestItem.PublicKeyA, IPAddress.Broadcast.ToString(), 30000 + i);
                _nodeTable.AddNode(node);
            }
            
            await _nodesLocator.LocateNodesAsync(CancellationToken.None);
        }
        
        [Test]
        public void Throws_when_uninitialized()
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => _nodesLocator.LocateNodesAsync(CancellationToken.None));
        }
    }
}
