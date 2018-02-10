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
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Nevermind.Core;
using Nevermind.Discovery.Lifecycle;
using Nevermind.Discovery.Messages;
using Nevermind.Discovery.RoutingTable;

namespace Nevermind.Discovery
{
    public class DiscoveryManager : IDiscoveryManager
    {
        private readonly ILogger _logger;
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly INodeLifecycleManagerFactory _nodeLifecycleManagerFactory;
        private readonly INodeFactory _nodeFactory;
        private readonly IMessageSerializer _messageSerializer;
        private readonly IUdpClient _udpClient;

        private readonly ConcurrentDictionary<MessageTypeKey, ManualResetEvent> _waitingEvents = new ConcurrentDictionary<MessageTypeKey, ManualResetEvent>();
        private readonly ConcurrentDictionary<string, INodeLifecycleManager> _nodeLifecycleManagers = new ConcurrentDictionary<string, INodeLifecycleManager>();

        public DiscoveryManager(ILogger logger, IDiscoveryConfigurationProvider configurationProvider, INodeLifecycleManagerFactory nodeLifecycleManagerFactory, INodeFactory nodeFactory, IMessageSerializer messageSerializer, IUdpClient udpClient)
        {
            _logger = logger;
            _configurationProvider = configurationProvider;
            _nodeLifecycleManagerFactory = nodeLifecycleManagerFactory;
            _nodeFactory = nodeFactory;
            _messageSerializer = messageSerializer;
            _udpClient = udpClient;
            _nodeLifecycleManagerFactory.DiscoveryManager = this;
            _udpClient.SubribeForMessages(this);
        }

        public void OnIncomingMessage(byte[] msg)
        {
            try
            {
                var message = _messageSerializer.Deserialize(msg);
                var msgType = message.MessageType;
                if (!msgType.HasValue)
                {
                    _logger.Error($"Unknown msgType: {(message.Type != null && message.Type.Any() ? message.Type[0].ToString() : "none")}");
                    return;
                }

                var node = _nodeFactory.CreateNode(message.GetNodeId(), message.Host, message.Port);
                var nodeManager = GetNodeLifecycleManager(node);

                switch (msgType.Value)
                {
                    case MessageType.Neighbors:
                        nodeManager.ProcessNeighborsMessage(message as NeighborsMessage);
                        break;
                    case MessageType.Pong:
                        nodeManager.ProcessPongMessage(message as PongMessage);
                        break;
                    case MessageType.Ping:
                        nodeManager.ProcessPingMessage(message as PingMessage);
                        break;
                    case MessageType.FindNode:
                        nodeManager.ProcessFindNodeMessage(message as FindNodeMessage);
                        break;
                    default:
                        _logger.Error($"Unsupported msgType: {msgType.Value}");
                        return;
                }

                NotifySubscribers(msgType.Value, nodeManager.ManagedNode);
            }
            catch (Exception e)
            {
                _logger.Error("Error during msg handling", e);
            }
        }

        public INodeLifecycleManager GetNodeLifecycleManager(Node node)
        {
            return _nodeLifecycleManagers.GetOrAdd(node.IdHashText, x => _nodeLifecycleManagerFactory.CreateNodeLifecycleManager(node));
        }

        public void SendMessage(Message message)
        {
            try
            {
                var host = message.Host;
                var port = message.Port;
                var msg = _messageSerializer.Serialize(message);
                _udpClient.SendMessage(host, port, msg);
            }
            catch (Exception e)
            {
                _logger.Error($"Error during sending message: {message}", e);
            }    
        }

        public bool WasMessageReceived(string senderIdHash, MessageType messageType, int timeout)
        {
            var resetEvent = GetResetEvent(senderIdHash, (int)messageType);
            var result = resetEvent.WaitOne(timeout);
            if (result)
            {
                resetEvent.Reset();
            }

            return result;
        }

        private void NotifySubscribers(MessageType msgType, Node node)
        {
            var resetEvent = GetResetEvent(node.IdHashText, (int)msgType);
            resetEvent.Set();
        }

        private ManualResetEvent GetResetEvent(string senderAddressHash, int messageType)
        {
            var key = new MessageTypeKey { SenderAddressHash = senderAddressHash, MessageType = messageType };
            var resetEvent = _waitingEvents.GetOrAdd(key, new ManualResetEvent(false));
            return resetEvent;
        }

        private struct MessageTypeKey
        {
            public string SenderAddressHash { get; set; }
            public int MessageType { get; set; }
        }
    }
}