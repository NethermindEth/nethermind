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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Discovery.Lifecycle;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Node = Nethermind.Discovery.RoutingTable.Node;
using PingMessage = Nethermind.Discovery.Messages.PingMessage;
using PongMessage = Nethermind.Discovery.Messages.PongMessage;

namespace Nethermind.Discovery
{
    public class DiscoveryManager : IDiscoveryManager
    {
        private readonly ILogger _logger;
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly INodeLifecycleManagerFactory _nodeLifecycleManagerFactory;
        private readonly INodeFactory _nodeFactory;
        private IMessageSender _messageSender;
        private readonly INodeTable _nodeTable;

        private readonly ConcurrentDictionary<MessageTypeKey, ManualResetEvent> _waitingEvents = new ConcurrentDictionary<MessageTypeKey, ManualResetEvent>();
        private readonly ConcurrentDictionary<string, INodeLifecycleManager> _nodeLifecycleManagers = new ConcurrentDictionary<string, INodeLifecycleManager>();

        public DiscoveryManager(
            ILogger logger,
            IDiscoveryConfigurationProvider configurationProvider,
            INodeLifecycleManagerFactory nodeLifecycleManagerFactory,
            INodeFactory nodeFactory, INodeTable nodeTable)
        {
            _logger = logger;
            _configurationProvider = configurationProvider;
            _nodeLifecycleManagerFactory = nodeLifecycleManagerFactory;
            _nodeFactory = nodeFactory;
            _nodeTable = nodeTable;
            _nodeLifecycleManagerFactory.DiscoveryManager = this;
        }

        public IMessageSender MessageSender { set => _messageSender = value; }

        public void OnIncomingMessage(DiscoveryMessage message)
        {
            try
            {
                _logger.Log($"Received msg: {message}");

                var msgType = message.MessageType;
               
                var node = _nodeFactory.CreateNode(message.FarPublicKey, message.FarAddress);
                var nodeManager = GetNodeLifecycleManager(node);

                switch (msgType)
                {
                    case MessageType.Neighbors:
                        nodeManager.ProcessNeighborsMessage(message as NeighborsMessage);
                        break;
                    case MessageType.Pong:
                        nodeManager.ProcessPongMessage(message as PongMessage);
                        break;
                    case MessageType.Ping:
                        var ping = message as PingMessage;
                        ValidatePingAddress(ping);
                        nodeManager.ProcessPingMessage(ping);
                        break;
                    case MessageType.FindNode:
                        nodeManager.ProcessFindNodeMessage(message as FindNodeMessage);
                        break;
                    default:
                        _logger.Error($"Unsupported msgType: {msgType}");
                        return;
                }

                NotifySubscribers(msgType, nodeManager.ManagedNode);
                CleanUpLifecycleManagers();
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

        public void SendMessage(DiscoveryMessage discoveryMessage)
        {
            try
            {
                _messageSender.SendMessage(discoveryMessage);
            }
            catch (Exception e)
            {
                _logger.Error($"Error during sending message: {discoveryMessage}", e);
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

        protected void ValidatePingAddress(PingMessage message)
        {
            if (message.DestinationAddress == null || message.SourceAddress == null || message.FarAddress == null)
            {
                throw new NetworkingException($"Received ping message with empty address, message: {message}");
            }
            if (!Bytes.UnsafeCompare(_nodeTable.MasterNode.Address.Address.GetAddressBytes(), message.DestinationAddress.Address.GetAddressBytes()))
            {
                //throw new NetworkingException($"Received message with inccorect destination adress, message: {message}");
            }
            if (_nodeTable.MasterNode.Port != message.DestinationAddress.Port)
            {
                throw new NetworkingException($"Received message with inccorect destination port, message: {message}");
            }
            if (!Bytes.UnsafeCompare(message.FarAddress.Address.GetAddressBytes(), message.SourceAddress.Address.GetAddressBytes()))
            {
                //throw new NetworkingException($"Received message with inccorect source adress, message: {message}");
            }
            if (message.FarAddress.Port != message.SourceAddress.Port)
            {
                throw new NetworkingException($"Received message with inccorect source port, message: {message}");
            }
        }

        private void NotifySubscribers(MessageType msgType, Node node)
        {
            var resetEvent = RemoveResetEvent(node.IdHashText, (int)msgType);
            resetEvent?.Set();
        }

        private ManualResetEvent GetResetEvent(string senderAddressHash, int messageType)
        {
            var key = new MessageTypeKey { SenderAddressHash = senderAddressHash, MessageType = messageType };
            var resetEvent = _waitingEvents.GetOrAdd(key, new ManualResetEvent(false));
            return resetEvent;
        }

        private ManualResetEvent RemoveResetEvent(string senderAddressHash, int messageType)
        {
            var key = new MessageTypeKey { SenderAddressHash = senderAddressHash, MessageType = messageType };
            return _waitingEvents.TryRemove(key, out var resetEvent) ? resetEvent : null;
        }

        private void CleanUpLifecycleManagers()
        {
            if (_nodeLifecycleManagers.Count <= _configurationProvider.MaxNodeLifecycleManagersCount)
            {
                return;
            }

            var cleanupCount = _configurationProvider.NodeLifecycleManagersCleaupCount;
            var activeExcluded = _nodeLifecycleManagers.Where(x => x.Value.State == NodeLifecycleState.ActiveExcluded).Take(cleanupCount).ToArray();
            if (activeExcluded.Length == cleanupCount)
            {
                var removeCounter = RemoveManagers(activeExcluded, activeExcluded.Length);
                _logger.Log($"Removed: {removeCounter} node lifecycle managers");
                return;
            }
            var unreachable = _nodeLifecycleManagers.Where(x => x.Value.State == NodeLifecycleState.Unreachable).Take(cleanupCount - activeExcluded.Length).ToArray();
            var removeCount = RemoveManagers(activeExcluded, activeExcluded.Length);
            removeCount = removeCount + RemoveManagers(unreachable, unreachable.Length);
            _logger.Log($"Removed: {removeCount} node lifecycle managers");
        }

        private int RemoveManagers(KeyValuePair<string, INodeLifecycleManager>[] items, int count)
        {
            var removeCount = 0;
            for (var i = 0; i < count; i++)
            {
                if (_nodeLifecycleManagers.TryRemove(items[i].Key, out var _))
                {
                    removeCount++;
                }
            }

            return removeCount;
        }

        private struct MessageTypeKey
        {
            public string SenderAddressHash { get; set; }
            public int MessageType { get; set; }
        }
    }
}