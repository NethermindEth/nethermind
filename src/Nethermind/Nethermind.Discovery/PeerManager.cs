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


using Nethermind.Core;
using Nethermind.Network;
using Nethermind.Network.Rlpx;

namespace Nethermind.Discovery
{
    public class PeerManager : IPeerManager
    {
        private readonly IRlpxPeer _localPeer;
        private readonly ILogger _logger;

        public PeerManager(IRlpxPeer localPeer, IDiscoveryManager discoveryManager, ILogger logger)
        {
            _localPeer = localPeer;
            _logger = logger;
            discoveryManager.NodeDiscovered += OnNodeDiscovered;
        }

        private void OnNodeDiscovered(object sender, NodeEventArgs nodeEventArgs)
        {
            DiscoveryNode node = nodeEventArgs.Node;
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Connecting to a newly discovered node {node.PublicKey} @ {node.Host}:{node.Port}");
            }

            _localPeer.ConnectAsync(node.PublicKey, node.Host, node.Port);
        }
    }
}