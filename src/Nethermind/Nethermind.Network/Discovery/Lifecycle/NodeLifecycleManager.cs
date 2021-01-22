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

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
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

        private PingMessage _lastSentPing;
        private bool _isNeighborsExpected;

        // private bool _receivedPing;
        private bool _sentPing;
        // private bool _sentPong;
        private bool _receivedPong;

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
        public bool IsBonded => _sentPing && _receivedPong;

        public event EventHandler<NodeLifecycleState> OnStateChanged;

        public void ProcessPingMessage(PingMessage discoveryMessage)
        {
            // _receivedPing = true;
            SendPong(discoveryMessage);

            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPingIn);
            RefreshNodeContactTime();
        }

        public void ProcessPongMessage(PongMessage discoveryMessage)
        {
            PingMessage sentPingMessage = Interlocked.Exchange(ref _lastSentPing, null);
            if (sentPingMessage == null)
            {
                return;
            }

            if (Bytes.AreEqual(sentPingMessage.Mdc, discoveryMessage.PingMdc))
            {
                _receivedPong = true;
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPongIn);
                if (IsBonded)
                {
                    UpdateState(NodeLifecycleState.Active);
                    if(_logger.IsDebug) _logger.Debug($"Bonded with {ManagedNode.Host}");
                }
                else
                {
                    if(_logger.IsDebug) _logger.Debug($"Bonding with {ManagedNode} failed.");
                }

                RefreshNodeContactTime();
            }
            else
            {
                if(_logger.IsDebug) _logger.Debug($"Unmatched MDC when bonding with {ManagedNode}");
                // ignore spoofed message
                _receivedPong = false;
                return;
            }
        }

        public void ProcessNeighborsMessage(NeighborsMessage discoveryMessage)
        {
            if (!IsBonded)
            {
                return;
            }

            if (_isNeighborsExpected)
            {
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryNeighboursIn);
                RefreshNodeContactTime();

                foreach (Node node in discoveryMessage.Nodes)
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
            if (!IsBonded)
            {
                return;
            }

            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryFindNodeIn);
            RefreshNodeContactTime();

            Node[] nodes = _nodeTable.GetClosestNodes(discoveryMessage.SearchedNodeId).ToArray();
            SendNeighbors(nodes);
        }
        
        private DateTime _lastTimeSendFindNode = DateTime.MinValue;

        public void SendFindNode(byte[] searchedNodeId)
        {
            if (!IsBonded)
            {
                if (_logger.IsDebug) _logger.Debug($"Sending FIND NODE on {ManagedNode} before bonding");
            }
            
            if (DateTime.UtcNow - _lastTimeSendFindNode < TimeSpan.FromSeconds(60))
            {
                return;
            }

            FindNodeMessage msg = _discoveryMessageFactory.CreateOutgoingMessage<FindNodeMessage>(ManagedNode);
            msg.SearchedNodeId = searchedNodeId;
            _isNeighborsExpected = true;
            _discoveryManager.SendMessage(msg);
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryFindNodeOut);
        }

        private DateTime _lastPingSent = DateTime.MinValue;

        public async Task SendPingAsync()
        {
            _lastPingSent = DateTime.UtcNow;
            _sentPing = true;
            await CreateAndSendPingAsync(_discoveryConfig.PingRetryCount);
        }

        public void SendPong(PingMessage discoveryMessage)
        {
            PongMessage msg = _discoveryMessageFactory.CreateOutgoingMessage<PongMessage>(ManagedNode);
            msg.PingMdc = discoveryMessage.Mdc;

            _discoveryManager.SendMessage(msg);
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPongOut);
            // _sentPong = true;
            if (IsBonded)
            {
                UpdateState(NodeLifecycleState.Active);
            }
        }

        public void SendNeighbors(Node[] nodes)
        {
            if (!IsBonded)
            {
                if (_logger.IsWarn) _logger.Warn("Attempt to send NEIGHBOURS before bonding");
                return;
            }

            NeighborsMessage msg = _discoveryMessageFactory.CreateOutgoingMessage<NeighborsMessage>(ManagedNode);
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
#pragma warning disable 4014
                SendPingAsync();
#pragma warning restore 4014
            }
            else if (newState == NodeLifecycleState.Active)
            {
                //TODO && !ManagedNode.IsDiscoveryNode - should we exclude discovery nodes
                //received pong first time
                if (State == NodeLifecycleState.New)
                {
                    NodeAddResult result = _nodeTable.AddNode(ManagedNode);
                    if (result.ResultType == NodeAddResultType.Full)
                    {
                        INodeLifecycleManager evictionCandidate = _discoveryManager.GetNodeLifecycleManager(result.EvictionCandidate.Node);
                        if (evictionCandidate != null)
                        {
                            _evictionManager.StartEvictionProcess(evictionCandidate, this);
                        }
                    }
                }
            }
            else if (newState == NodeLifecycleState.EvictCandidate)
            {
                if (State == NodeLifecycleState.EvictCandidate)
                {
                    throw new InvalidOperationException("Cannot start more than one eviction process on same node.");
                }

                if (DateTime.UtcNow - _lastPingSent > TimeSpan.FromSeconds(5))
                {
#pragma warning disable 4014
                    SendPingAsync();
#pragma warning restore 4014
                }
                else
                {
                    OnStateChanged?.Invoke(this, NodeLifecycleState.Active);
                }
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

        private async Task CreateAndSendPingAsync(int counter = 1)
        {
            PingMessage msg = _discoveryMessageFactory.CreateOutgoingMessage<PingMessage>(ManagedNode);
            msg.SourceAddress = _nodeTable.MasterNode.Address;
            msg.DestinationAddress = msg.FarAddress;

            try
            {
                _lastSentPing = msg;
                _discoveryManager.SendMessage(msg);
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPingOut);

                bool result = await _discoveryManager.WasMessageReceived(ManagedNode.IdHash, MessageType.Pong, _discoveryConfig.PongTimeout);
                if (!result)
                {
                    if (counter > 1)
                    {
                        await CreateAndSendPingAsync(counter - 1);
                    }

                    UpdateState(NodeLifecycleState.Unreachable);
                }
            }
            catch (Exception e)
            {
                _logger.Error("Error during sending ping message", e);
            }
        }
    }
}
