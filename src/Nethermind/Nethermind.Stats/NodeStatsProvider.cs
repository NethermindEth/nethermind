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

using System.Collections.Concurrent;
using Nethermind.Core.Model;
using Nethermind.Network.Discovery.RoutingTable;

namespace Nethermind.Stats
{
    public class NodeStatsProvider : INodeStatsProvider
    {
        private readonly IStatsConfig _statsConfig;
        private readonly INodeFactory _nodeFactory;
        private readonly ConcurrentDictionary<NodeId, INodeStats> _nodeStats = new ConcurrentDictionary<NodeId, INodeStats>();

        public NodeStatsProvider(IStatsConfig statsConfig, INodeFactory nodeFactory)
        {
            _statsConfig = statsConfig;
            _nodeFactory = nodeFactory;
        }

        public INodeStats GetOrAddNodeStats(Node node)
        {
            return _nodeStats.GetOrAdd(node.Id, x => new NodeStats(node, _statsConfig));
        }

        public INodeStats GetOrAddNodeStats(NodeId nodeId, string host, int port)
        {
            var node = _nodeFactory.CreateNode(nodeId, host, port);
            return GetOrAddNodeStats(node);
        }
    }
}