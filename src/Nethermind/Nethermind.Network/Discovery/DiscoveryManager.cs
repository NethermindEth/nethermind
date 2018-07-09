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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;

namespace Nethermind.Network.Discovery
{
    public class DiscoveryManager : IDiscoveryManager
    {
        private readonly INetworkConfig _configurationProvider;
        private readonly ILogger _logger;
        private readonly INodeFactory _nodeFactory;
        private readonly INodeLifecycleManagerFactory _nodeLifecycleManagerFactory;
        private readonly ConcurrentDictionary<string, INodeLifecycleManager> _nodeLifecycleManagers = new ConcurrentDictionary<string, INodeLifecycleManager>();
        private readonly INodeTable _nodeTable;
        private readonly IDiscoveryStorage _discoveryStorage;

        private readonly ConcurrentDictionary<MessageTypeKey, ManualResetEvent> _waitingEvents = new ConcurrentDictionary<MessageTypeKey, ManualResetEvent>();
        private IMessageSender _messageSender;

        public DiscoveryManager(INodeLifecycleManagerFactory nodeLifecycleManagerFactory,
            INodeFactory nodeFactory,
            INodeTable nodeTable,
            IDiscoveryStorage discoveryStorage,
            IConfigProvider configurationProvider,
            ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _configurationProvider = configurationProvider.GetConfig<NetworkConfig>();
            _nodeLifecycleManagerFactory = nodeLifecycleManagerFactory;
            _nodeFactory = nodeFactory;
            _nodeTable = nodeTable;
            _discoveryStorage = discoveryStorage;
            _nodeLifecycleManagerFactory.DiscoveryManager = this;
        }

        public IMessageSender MessageSender
        {
            set => _messageSender = value;
        }

        public void OnIncomingMessage(DiscoveryMessage message)
        {
            try
            {
                _logger.Debug($"Received msg: {message}");

                MessageType msgType = message.MessageType;

                Node node = _nodeFactory.CreateNode(new NodeId(message.FarPublicKey), message.FarAddress);
                INodeLifecycleManager nodeManager = GetNodeLifecycleManager(node);

                switch (msgType)
                {
                    case MessageType.Neighbors:
                        nodeManager.ProcessNeighborsMessage(message as NeighborsMessage);
                        break;
                    case MessageType.Pong:
                        nodeManager.ProcessPongMessage(message as PongMessage);
                        break;
                    case MessageType.Ping:
                        PingMessage ping = message as PingMessage;
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

                NotifySubscribersOnMsgReceived(msgType, nodeManager.ManagedNode);
                CleanUpLifecycleManagers();
            }
            catch (Exception e)
            {
                _logger.Error("Error during msg handling", e);
            }
        }

        public INodeLifecycleManager GetNodeLifecycleManager(Node node, bool isPersisted = false)
        {
            if (_nodeTable.MasterNode.Equals(node))
            {
                return null;
            }
            return _nodeLifecycleManagers.GetOrAdd(node.IdHashText, x =>
            {
                var manager = _nodeLifecycleManagerFactory.CreateNodeLifecycleManager(node);
                if (!isPersisted)
                {
                    _discoveryStorage.UpdateNodes(new[] { manager });
                }
                OnNewNode(manager);
                return manager;
            });
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
            ManualResetEvent resetEvent = GetResetEvent(senderIdHash, (int)messageType);
            bool result = resetEvent.WaitOne(timeout);
            if (result)
            {
                resetEvent.Reset();
            }

            return result;
        }

        public event EventHandler<NodeEventArgs> NodeDiscovered;

        public IReadOnlyCollection<INodeLifecycleManager> GetNodeLifecycleManagers()
        {
            return _nodeLifecycleManagers.Values.ToArray();
        }

        public IReadOnlyCollection<INodeLifecycleManager> GetNodeLifecycleManagers(Func<INodeLifecycleManager, bool> query)
        {
            return _nodeLifecycleManagers.Values.Where(query.Invoke).ToArray();
        }

        protected void ValidatePingAddress(PingMessage message)
        {
            if (message.DestinationAddress == null || message.SourceAddress == null || message.FarAddress == null)
            {
                if (_logger.IsWarnEnabled)
                {
                    _logger.Warn($"Received ping message with empty address, message: {message}");
                }
            }

            if (!Bytes.UnsafeCompare(_nodeTable.MasterNode.Address.Address.GetAddressBytes(), message.DestinationAddress.Address.GetAddressBytes()))
            {
                //throw new NetworkingException($"Received message with inccorect destination adress, message: {message}");
            }

            if (_nodeTable.MasterNode.Port != message.DestinationAddress.Port)
            {
//                throw new NetworkingException($"Received message with inccorect destination port, message: {message}");
            }

            if (!Bytes.UnsafeCompare(message.FarAddress.Address.GetAddressBytes(), message.SourceAddress.Address.GetAddressBytes()))
            {
                //throw new NetworkingException($"Received message with inccorect source adress, message: {message}");
            }

            if (message.FarAddress.Port != message.SourceAddress.Port)
            {
                if (_logger.IsWarnEnabled)
                {
                    _logger.Warn($"Received message with incorect source port, message: {message}");
                }
            }
        }

        private void OnNewNode(INodeLifecycleManager manager)
        {
            NodeDiscovered?.Invoke(this, new NodeEventArgs(manager));
        }

        private void NotifySubscribersOnMsgReceived(MessageType msgType, Node node)
        {
            ManualResetEvent resetEvent = RemoveResetEvent(node.IdHashText, (int)msgType);
            resetEvent?.Set();
        }

        private ManualResetEvent GetResetEvent(string senderAddressHash, int messageType)
        {
            MessageTypeKey key = new MessageTypeKey(senderAddressHash, messageType);
            ManualResetEvent resetEvent = _waitingEvents.GetOrAdd(key, new ManualResetEvent(false));
            return resetEvent;
        }

        private ManualResetEvent RemoveResetEvent(string senderAddressHash, int messageType)
        {
            MessageTypeKey key = new MessageTypeKey(senderAddressHash, messageType);
            return _waitingEvents.TryRemove(key, out ManualResetEvent resetEvent) ? resetEvent : null;
        }

        private void CleanUpLifecycleManagers()
        {
            if (_nodeLifecycleManagers.Count <= _configurationProvider.MaxNodeLifecycleManagersCount)
            {
                return;
            }

            int cleanupCount = _configurationProvider.NodeLifecycleManagersCleaupCount;
            KeyValuePair<string, INodeLifecycleManager>[] activeExcluded = _nodeLifecycleManagers.Where(x => x.Value.State == NodeLifecycleState.ActiveExcluded).Take(cleanupCount).ToArray();
            if (activeExcluded.Length == cleanupCount)
            {
                int removeCounter = RemoveManagers(activeExcluded, activeExcluded.Length);
                _logger.Info($"Removed: {removeCounter} activeExcluded node lifecycle managers");
                return;
            }

            KeyValuePair<string, INodeLifecycleManager>[] unreachable = _nodeLifecycleManagers.Where(x => x.Value.State == NodeLifecycleState.Unreachable).Take(cleanupCount - activeExcluded.Length).ToArray();
            int removeCount = RemoveManagers(activeExcluded, activeExcluded.Length);
            removeCount = removeCount + RemoveManagers(unreachable, unreachable.Length);
            _logger.Info($"Removed: {removeCount} unreachable node lifecycle managers");
        }

        private int RemoveManagers(KeyValuePair<string, INodeLifecycleManager>[] items, int count)
        {
            int removeCount = 0;
            for (var i = 0; i < count; i++)
            {
                var item = items[i];
                if (_nodeLifecycleManagers.TryRemove(item.Key, out var _))
                {
                    _discoveryStorage.RemoveNodes(new[] { item.Value });
                    removeCount++;
                }
            }

            return removeCount;
        }

        private struct MessageTypeKey : IEquatable<MessageTypeKey>
        {
            public string SenderAddressHash { get; }
            public int MessageType { get; }

            public MessageTypeKey(string senderAddressHash, int messageType)
            {
                SenderAddressHash = senderAddressHash;
                MessageType = messageType;
            }
            
            public bool Equals(MessageTypeKey other)
            {
                return string.Equals(SenderAddressHash, other.SenderAddressHash) && MessageType == other.MessageType;
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                return obj is MessageTypeKey && Equals((MessageTypeKey)obj);
            }

            [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
            public override int GetHashCode()
            {
                unchecked
                {
                    return ((SenderAddressHash != null ? SenderAddressHash.GetHashCode() : 0) * 397) ^ MessageType;
                }
            }
        }
    }
}