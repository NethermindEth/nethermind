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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Model;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Stats;

namespace Nethermind.Network.Discovery.Lifecycle
{
    public class NodeLifecycleManagerFactory : INodeLifecycleManagerFactory
    {
        private readonly INodeFactory _nodeFactory;
        private readonly INodeTable _nodeTable;
        private readonly ILogger _logger;
        private readonly INetworkConfigurationProvider _networkConfigurationProvider;
        private readonly IDiscoveryMessageFactory _discoveryMessageFactory;
        private readonly IEvictionManager _evictionManager;
        private readonly INodeStatsProvider _nodeStatsProvider;

        public NodeLifecycleManagerFactory(INodeFactory nodeFactory, INodeTable nodeTable, ILogger logger, INetworkConfigurationProvider networkConfigurationProvider, IDiscoveryMessageFactory discoveryMessageFactory, IEvictionManager evictionManager, INodeStatsProvider nodeStatsProvider)
        {
            _nodeFactory = nodeFactory;
            _nodeTable = nodeTable;
            _logger = logger;
            _networkConfigurationProvider = networkConfigurationProvider;
            _discoveryMessageFactory = discoveryMessageFactory;
            _evictionManager = evictionManager;
            _nodeStatsProvider = nodeStatsProvider;
        }

        public IDiscoveryManager DiscoveryManager { private get; set; }

        public INodeLifecycleManager CreateNodeLifecycleManager(Node node)
        {
            if (DiscoveryManager == null)
            {
                throw new Exception($"{nameof(DiscoveryManager)} has to be set");
            }
            
            return new NodeLifecycleManager(node, DiscoveryManager, _nodeTable, _logger, _networkConfigurationProvider, _discoveryMessageFactory, _evictionManager, _nodeStatsProvider.GetNodeStats(node.Id));
        }

        public INodeLifecycleManager CreateNodeLifecycleManager(NodeId id, string host, int port)
        {
            if (DiscoveryManager == null)
            {
                throw new Exception($"{nameof(DiscoveryManager)} has to be set");
            }
            
            var node = _nodeFactory.CreateNode(id, host, port);
            return new NodeLifecycleManager(node, DiscoveryManager, _nodeTable, _logger, _networkConfigurationProvider, _discoveryMessageFactory, _evictionManager, _nodeStatsProvider.GetNodeStats(id));
        }
    }
}