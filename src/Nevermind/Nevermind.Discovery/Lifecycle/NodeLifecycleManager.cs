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
using Nevermind.Core;
using Nevermind.Discovery.Messages;
using Nevermind.Discovery.RoutingTable;

namespace Nevermind.Discovery.Lifecycle
{
    public class NodeLifecycleManager : INodeLifecycleManager
    {
        private readonly IDiscoveryManager _discoveryManager;
        private readonly INodeTable _nodeTable;
        private readonly ILogger _logger;
        private readonly IDiscoveryConfigurationProvider _discoveryConfigurationProvider;
        private readonly IMessageFactory _messageFactory;
        private readonly IEvictionManager _evictionManager;

        private int _pingRetryCount;
        private bool _isPongExpected;
        private bool _isNeighborsExpected;

        public NodeLifecycleManager(Node node, IDiscoveryManager discoveryManager, INodeTable nodeTable, ILogger logger, IDiscoveryConfigurationProvider discoveryConfigurationProvider, IMessageFactory messageFactory, IEvictionManager evictionManager)
        {
            _discoveryManager = discoveryManager;
            _nodeTable = nodeTable;
            _logger = logger;
            _discoveryConfigurationProvider = discoveryConfigurationProvider;
            _messageFactory = messageFactory;
            _evictionManager = evictionManager;
            ManagedNode = node;
            _pingRetryCount = _discoveryConfigurationProvider.PingRetryCount;
            UpdateState(NodeLifecycleState.New);
        }

        public Node ManagedNode { get; }
        public NodeLifecycleState State { get; private set; }
        public event EventHandler<NodeLifecycleState> OnStateChanged;

        public void ProcessPingMessage(PingMessage discoveryMessage)
        {
            SendPong();
        }

        public void ProcessPongMessage(PongMessage discoveryMessage)
        {
            if (_isPongExpected)
            {
                UpdateState(NodeLifecycleState.Active);
            }

            _isPongExpected = false;
        }

        public void ProcessNeighborsMessage(NeighborsMessage discoveryMessage)
        {
            if (_isNeighborsExpected)
            {
                foreach (var node in discoveryMessage.Nodes)
                {
                    //If node is new it will create a new nodeLifecycleManager and will update state to New, which will trigger Ping
                    _discoveryManager.GetNodeLifecycleManager(node);
                }
            }

            _isNeighborsExpected = false;
        }

        public void ProcessFindNodeMessage(FindNodeMessage discoveryMessage)
        {
            var nodes = _nodeTable.GetClosestNodes();
            SendNeighbors(nodes);
        }

        public void SendFindNode(Node searchedNode)
        {
            var msg = _messageFactory.CreateMessage<FindNodeMessage>(ManagedNode);
            msg.SearchedNode = searchedNode;
            _isNeighborsExpected = true;
            _discoveryManager.SendMessage(msg);
        }

        public async void SendPing()
        {
            _isPongExpected = true;
            await Task.Run(() => SendPingSync());
        }

        public void SendPong()
        {
            var msg = _messageFactory.CreateMessage<PongMessage>(ManagedNode);
            _discoveryManager.SendMessage(msg);
        }

        public void SendNeighbors(Node[] nodes)
        {
            var msg = _messageFactory.CreateMessage<NeighborsMessage>(ManagedNode);
            msg.Nodes = nodes;
            _discoveryManager.SendMessage(msg);
        }

        public void StartEvictionProcess()
        {
            UpdateState(NodeLifecycleState.EvictCandidate);
        }

        private void UpdateState(NodeLifecycleState newState)
        {
            if (newState == NodeLifecycleState.New)
            {
                //if node is just discovered we send ping to confirm it is active
                SendPing();
            }

            if (newState == NodeLifecycleState.Active)
            {
                //TODO && !ManagedNode.IsDicoveryNode - should we exclude discovery nodes
                if (State == NodeLifecycleState.New)
                {
                    var result = _nodeTable.AddNode(ManagedNode);
                    if (result.ResultType == NodeAddResultType.Full)
                    {
                        var evictionCandidate = _discoveryManager.GetNodeLifecycleManager(result.EvictionCandidate.Node);
                        _evictionManager.StartEvictionProcess(evictionCandidate, this);
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

        private void SendPingSync()
        {
            try
            {
                var msg = _messageFactory.CreateMessage<PingMessage>(ManagedNode);
                _discoveryManager.SendMessage(msg);

                if (_discoveryManager.WasMessageReceived(ManagedNode.IdHashText, MessageType.Pong, _discoveryConfigurationProvider.PongTimeout))
                {
                    _pingRetryCount = _discoveryConfigurationProvider.PingRetryCount;
                }
                else
                {
                    if (_pingRetryCount > 1)
                    {
                        _pingRetryCount = _pingRetryCount - 1;
                        SendPing();
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