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
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Lifecycle
{
    public class NodeLifecycleManager : INodeLifecycleManager
    {
        private readonly IDiscoveryManager _discoveryManager;
        private readonly INodeTable _nodeTable;
        private readonly ILogger _logger;
        private readonly IDiscoveryConfig _discoveryConfig;
        private readonly IDiscoveryMessageFactory _discoveryMessageFactory;
        private readonly IEvictionManager _evictionManager;

        private bool _isPongExpected;
        private bool _isNeighborsExpected;

        public NodeLifecycleManager(Node node, IDiscoveryManager discoveryManager, INodeTable nodeTable, IDiscoveryMessageFactory discoveryMessageFactory, IEvictionManager evictionManager, INodeStats nodeStats, IDiscoveryConfig discoveryConfig, ILogger logger)
        {
            _discoveryManager = discoveryManager;
            _nodeTable = nodeTable;
            _logger = logger;
            _discoveryConfig = discoveryConfig;
            _discoveryMessageFactory = discoveryMessageFactory;
            _evictionManager = evictionManager;
            NodeStats = nodeStats;
            ManagedNode = node;
            UpdateState(NodeLifecycleState.New);
        }

        public Node ManagedNode { get; }
        public NodeLifecycleState State { get; private set; }
        public INodeStats NodeStats { get; }

        public event EventHandler<NodeLifecycleState> OnStateChanged;

        public void ProcessPingMessage(PingMessage discoveryMessage)
        {
            SendPong(discoveryMessage);

            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPingIn);
            RefreshNodeContactTime();
            
        }

        public void ProcessPongMessage(PongMessage discoveryMessage)
        {
            if (_isPongExpected)
            {
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPongIn);
                RefreshNodeContactTime();

                UpdateState(NodeLifecycleState.Active);
            }

            _isPongExpected = false;
        }

        public void ProcessNeighborsMessage(NeighborsMessage discoveryMessage)
        {
            if (_isNeighborsExpected)
            {
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryNeighboursIn);
                RefreshNodeContactTime();

                foreach (var node in discoveryMessage.Nodes)
                {
                    if (node.Address.Address.ToString().Contains("127.0.0.1"))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Received localhost as node address from: {discoveryMessage.FarPublicKey}, node: {node}"); 
                        continue;
                    }
                    //If node is new it will create a new nodeLifecycleManager and will update state to New, which will trigger Ping
                    _discoveryManager.GetNodeLifecycleManager(node);
                }
            }

            _isNeighborsExpected = false;
        }

        public void ProcessFindNodeMessage(FindNodeMessage discoveryMessage)
        {
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryFindNodeIn);
            RefreshNodeContactTime();

            var nodes = _nodeTable.GetClosestNodes(discoveryMessage.SearchedNodeId);
            SendNeighbors(nodes);
        }

        public void SendFindNode(byte[] searchedNodeId)
        {
            var msg = _discoveryMessageFactory.CreateOutgoingMessage<FindNodeMessage>(ManagedNode);
            msg.SearchedNodeId = searchedNodeId;
            _isNeighborsExpected = true;
            _discoveryManager.SendMessage(msg);
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryFindNodeOut);
        }

        public void SendPing()
        {
            _isPongExpected = true;
            Task.Run(() => SendPingAsync(_discoveryConfig.PingRetryCount));
        }

        public void SendPong(PingMessage discoveryMessage)
        {
            var msg = _discoveryMessageFactory.CreateOutgoingMessage<PongMessage>(ManagedNode);
            msg.PingMdc = discoveryMessage.Mdc;
            _discoveryManager.SendMessage(msg);
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPongOut);
        }

        public void SendNeighbors(Node[] nodes)
        {
            var msg = _discoveryMessageFactory.CreateOutgoingMessage<NeighborsMessage>(ManagedNode);
            msg.Nodes = nodes;
            _discoveryManager.SendMessage(msg);
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryNeighboursOut);
        }

        public void StartEvictionProcess()
        {
            UpdateState(NodeLifecycleState.EvictCandidate);
        }

        public void LostEvictionProcess()
        {
            if (State == NodeLifecycleState.Active)
            {
                UpdateState(NodeLifecycleState.ActiveExcluded);
            }
        }

        private void UpdateState(NodeLifecycleState newState)
        {
            if (newState == NodeLifecycleState.New)
            {
                //if node is just discovered we send ping to confirm it is active
                SendPing();
            }
            else if (newState == NodeLifecycleState.Active)
            {
                //TODO && !ManagedNode.IsDicoveryNode - should we exclude discovery nodes
                //received pong first time
                if (State == NodeLifecycleState.New)
                {
                    var result = _nodeTable.AddNode(ManagedNode);
                    if (result.ResultType == NodeAddResultType.Full)
                    {
                        var evictionCandidate = _discoveryManager.GetNodeLifecycleManager(result.EvictionCandidate.Node);
                        if (evictionCandidate != null)
                        {
                            _evictionManager.StartEvictionProcess(evictionCandidate, this);
                        }
                    }
                }
            }
            else if (newState == NodeLifecycleState.EvictCandidate)
            {
                SendPing();
            }

            State = newState;
            OnStateChanged?.Invoke(this, State);
        }

        private void RefreshNodeContactTime()
        {
            if (State == NodeLifecycleState.Active)
            {
                _nodeTable.RefreshNode(ManagedNode);
            }
        }

        private async Task SendPingAsync(int counter)
        {
            try
            {
                var msg = _discoveryMessageFactory.CreateOutgoingMessage<PingMessage>(ManagedNode);         
                msg.Version = _discoveryConfig.PingMessageVersion;
                msg.SourceAddress = _nodeTable.MasterNode.Address;
                msg.DestinationAddress = msg.FarAddress;
                _discoveryManager.SendMessage(msg);
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPingOut);

                var result = await _discoveryManager.WasMessageReceived(ManagedNode.IdHash, MessageType.Pong, _discoveryConfig.PongTimeout);
                if (!result)
                {
                    if (counter > 1)
                    {
                        await SendPingAsync(counter - 1);
                    }
                    else
                    {
                        UpdateState(NodeLifecycleState.Unreachable);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error("Error during sending ping message", e);
            }
        }
    }
}