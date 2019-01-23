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
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Logging;
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
        private readonly INetworkConfig _networkConfig;
        private readonly IDiscoveryMessageFactory _discoveryMessageFactory;
        private readonly IEvictionManager _evictionManager;
        private readonly ITicketProvider _ticketProvider;

        private bool _isPongExpected;
        private bool _isNeighborsExpected;

        private List<Ticket> _pingTickets;

        public NodeLifecycleManager(Node node, IDiscoveryManager discoveryManager, INodeTable nodeTable, ILogger logger, INetworkConfig networkConfig, IDiscoveryMessageFactory discoveryMessageFactory, IEvictionManager evictionManager, INodeStats nodeStats)
        {
            _discoveryManager = discoveryManager;
            _nodeTable = nodeTable;
            _logger = logger;
            _networkConfig = networkConfig;
            _discoveryMessageFactory = discoveryMessageFactory;
            _evictionManager = evictionManager;
            NodeStats = nodeStats;
            ManagedNode = node;
            _ticketProvider = discoveryManager.ticketProvider;
            UpdateState(NodeLifecycleState.New);
        }

        public Node ManagedNode { get; }
        public NodeLifecycleState State { get; private set; }
        public INodeStats NodeStats { get; }

        public event EventHandler<NodeLifecycleState> OnStateChanged;

        public void ProcessPingMessage(PingMessage discoveryMessage)
        {
            SendPong(discoveryMessage);

            _pingTickets = new List<Ticket>(discoveryMessage.Topics);
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

            try {
                ticket = pongToTicket(_timestamp.GetTimestamp(), _pingTickets, ManagedNode, discoveryMessage);
                _ticketProvider.addTicket(_timestamp.GetTimestamp(), discoveryMessage.PingMdc, ticket);
            } catch (Exception e) {
                _logger.Trace($"Failure to convert pong to ticket, {e.Message}");
            }
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

        public void ProcessFindNodeHashMessage(FindNodeHashMessage discoveryMessage)
        {
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryFindNodeHashIn);
            RefreshNodeContactTime();

            var nodes = _nodeTable.GetClosestNodes(new Keccak(discoveryMessage.SearchedNodeIdHash));
            SendNeighbors(nodes);
        }

        public void ProcessTopicRegisterMessage(TopicRegisterMessage discoveryMessage) {
            _logger.Trace("got TopicRegisterPacket");
            NodeStats.AddNoteStatsEvent(NodeStatsEventType.DiscoveryTopicRegisterIn);
            RefreshNodeContactTime();

            try {
                ValidateTopicRegister(discoveryMessage); //TODO: ValidateTopicRegister
            } catch (Exception e) {
                    _logger.Trace($"Bad waiting ticket: { e.ToString() }");
            } finally {
                _topicTable.useTicket(ManagedNode, 
                                      discoveryMessage.Pong.TicketSerial,
                                      discoveryMessage.Topics,
                                      Int32(idx),
                                      discoveryMessage.Pong.Expiration,
                                      discoveryMessage.WaitPeriods
                            );
            }
            // TODO: Change Node state appropriately    
        }
        public void ProcessTopicQueryMessage(TopicQueryMessage discoveryMessage) {
            NodeStats.AddNoteStatsEvent(NodeStatsEventType.DiscoveryTopicQueryIn);
            RefreshNodeContactTime();

            Topic topic = discoveryMessage.Topic;

            ICollection<Node> results = _topicTable.getEntries(topic);

            if (_ticketProvider.tickets.ContainsKey(topic)) {
                results.Append(_nodeTable.MasterNode);
            }
            if (results.Count() > 10) {
                results = results.GetRange(0, 10);
            }

            //_topicTable.
            SendTopicNodes(discoveryMessage, results);
            // TODO: Change Node state appropriately
        }

        public void ProcessTopicNodesMessage(TopicNodesMessage topicNodesMessage) {
            NodeStats.AddNoteStatsEvent(NodeStatsEventType.DiscoveryTopicNodesIn);
            RefreshNodeContactTime();

            if (_ticketProvider.queriesSent.ContainsKey(ManagedNode)) {
                Task.Run(() => gotTopicNodes(ManagedNode, topicNodesMessage.TopicQueryMdc, topicNodesMessage.Nodes));
            }
            //TODO: Change node state appropriately
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
            Task.Run(() => SendPingAsync(_networkConfig.PingRetryCount));
        }

        public void SendPong(PingMessage discoveryMessage)
        {
            var msg = _discoveryMessageFactory.CreateOutgoingMessage<PongMessage>(ManagedNode);
            Ticket t = _topicTable.getTIcket(ManagedNode, discoveryMessage.Topics);

            ticketToPong(t, msg);
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

        // TODO: add to _waitingEvents
        public void SendTopicQuery(Topic topic)
        {
            var msg = _discoveryMessageFactory.CreateOutgoingMessage<TopicQueryMessage>(ManagedNode);
            msg.Topic = topic;
            _discoveryManager.SendMessage(msg);
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.TopicQueryOut);
        }

        public void SendTopicRegister(ICollection<Topic> topics, UInt16 idx, byte[] pong) {
            var msg = _discoveryMessageFactory.CreateOutgoingMesasge<TopicRegisterMessage>(ManagedNode);
            msg.Topics = topics;
            msg.Idx = idx;
            msg.Pong = pong;
            _discoveryManager.SendMessage(msg);
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.TopicRegisterOut);
        }

        private void SendTopicNodes(TopicQueryMessage topicQueryMessage, ICollection<Node> results) {
            var msg = _discoveryMessageFactory.CreateOutgoingMesasge<TopicNodesMessage>(ManagedNode);
            msg.TopicQueryMdc = topicQueryMessage.Mdc;
            msg.Nodes = results;
            _discoveryManager.SendMessage(msg);
            NodeStats.AddNodeStatsEvent(NodeStatsEventType.TopicNodesOut);
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
                msg.Version = _networkConfig.PingMessageVersion;
                msg.SourceAddress = _nodeTable.MasterNode.Address;
                msg.DestinationAddress = msg.FarAddress;
                msg.Topics = _ticketProvider.regTopicSet(); 
                _discoveryManager.SendMessage(msg);
                NodeStats.AddNodeStatsEvent(NodeStatsEventType.DiscoveryPingOut);

                var result = await _discoveryManager.WasMessageReceived(ManagedNode.IdHash, MessageType.Pong, _networkConfig.PongTimeout);
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

        private Ticket pongToTicket(long localTime, ICollection<Topic> topics, Node node, PongMessage pong) {
            ICollection<int> wps = pong.WaitPeriods;
            if (topics.Count() != wps.Count()) {
                throw new Exception($"bad wait period list; got {wps.Count()} values want {topics.Count()}");
            }
            string[] topicStrings = new string[topics.Count()];
            for (int i = 0; i < topics.Count(); i++) {
                topicStrings[i] = topics[i].ToString();
            } 
            if (new Keccak(_rlp.Encode(topicStrings)) != pong.TopicHash) {
                throw new Exception("bad topic hash");
            }
            List<long> regTime = new List<long>();
            for (int i = 0; i < wps.Count(); i++) {
                wp = wps[i];
                regTime[i] = localTime + (new TimeSpan(0, 0, 1)).Ticks*wp;
            }
            Ticket t = new Ticket(localTime, node, topics, pong, regTime);
            return t;
        }

        private void ticketToPong(Ticket t, PongMessage pong) {
            pong.Expiration = UInt64(t.issueTime / (new TimeSpan(0, 0, 1).Ticks));
            for (int i = 0; i < t.topics.Count(); i++) {
                topicStrings[i] = t.topics[i].ToString();
            } 
            pong.TopicHash = new Keccak(_rlp.Encode(topicStrings));
            pong.TicketSerial = t.serial;
            List<UInt32> waitPeriods = new List<UInt32>();
            for (int i = 0; i < t.regTime.Count(); i++) {
                waitPeriods[i] = UInt32(new TimeSpan(regTime - t.issueTime).TotalSeconds);
            }
            pong.WaitPeriods = waitPeriods;
        }
    }
}