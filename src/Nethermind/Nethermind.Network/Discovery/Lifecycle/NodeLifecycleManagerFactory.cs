/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Lifecycle
{
    public class NodeLifecycleManagerFactory : INodeLifecycleManagerFactory
    {
        private readonly INodeTable _nodeTable;
        private readonly ILogger _logger;
        private readonly INetworkConfig _networkConfig;
        private readonly IDiscoveryMessageFactory _discoveryMessageFactory;
        private readonly IEvictionManager _evictionManager;
        private readonly INodeStatsManager _nodeStatsManager;

        public NodeLifecycleManagerFactory(INodeTable nodeTable, IDiscoveryMessageFactory discoveryMessageFactory, IEvictionManager evictionManager, INodeStatsManager nodeStatsManager, INetworkConfig networkConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _discoveryMessageFactory = discoveryMessageFactory ?? throw new ArgumentNullException(nameof(discoveryMessageFactory));
            _evictionManager = evictionManager ?? throw new ArgumentNullException(nameof(evictionManager));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
        }

        public IDiscoveryManager DiscoveryManager { private get; set; }

        public INodeLifecycleManager CreateNodeLifecycleManager(Node node)
        {
            if (DiscoveryManager == null)
            {
                throw new Exception($"{nameof(DiscoveryManager)} has to be set");
            }
            
            return new NodeLifecycleManager(node, DiscoveryManager, _nodeTable, _logger, _networkConfig, _discoveryMessageFactory, _evictionManager, _nodeStatsManager.GetOrAdd(node));
        }

        public INodeLifecycleManager CreateNodeLifecycleManager(PublicKey id, string host, int port)
        {
            if (DiscoveryManager == null)
            {
                throw new Exception($"{nameof(DiscoveryManager)} has to be set");
            }
            
            var node = new Node(id, host, port);
            return new NodeLifecycleManager(node, DiscoveryManager, _nodeTable, _logger, _networkConfig, _discoveryMessageFactory, _evictionManager, _nodeStatsManager.GetOrAdd(node));
        }
    }
}