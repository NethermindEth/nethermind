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
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats;
using Nethermind.Stats.Model;

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
        private readonly INetworkStorage _discoveryStorage;

        private readonly ConcurrentDictionary<MessageTypeKey, TaskCompletionSource<DiscoveryMessage>> _waitingEvents = new ConcurrentDictionary<MessageTypeKey, TaskCompletionSource<DiscoveryMessage>>();
        private IMessageSender _messageSender;

        public DiscoveryManager(INodeLifecycleManagerFactory nodeLifecycleManagerFactory,
            INodeFactory nodeFactory,
            INodeTable nodeTable,
            INetworkStorage discoveryStorage,
            IConfigProvider configurationProvider,
            ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _configurationProvider = configurationProvider.GetConfig<INetworkConfig>();
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
                if(_logger.IsTrace) _logger.Trace($"Received msg: {message}");

                MessageType msgType = message.MessageType;

                Node node = _nodeFactory.CreateNode(new NodeId(message.FarPublicKey), message.FarAddress);
                INodeLifecycleManager nodeManager = GetNodeLifecycleManager(node);
                if (nodeManager == null)
                {
                    return;
                }

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

                NotifySubscribersOnMsgReceived(msgType, nodeManager.ManagedNode, message);
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

            if (node.Port == 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Node is not listening - Port 0, blocking add to discovery, id: {node.Id}");
                return null;
            }

            return _nodeLifecycleManagers.GetOrAdd(node.IdHashText, x =>
            {
                var manager = _nodeLifecycleManagerFactory.CreateNodeLifecycleManager(node);
                if (!isPersisted)
                {
                    _discoveryStorage.UpdateNodes(new[] { new NetworkNode(manager.ManagedNode.Id.PublicKey, manager.ManagedNode.Host, manager.ManagedNode.Port, manager.ManagedNode.Description, manager.NodeStats.NewPersistedNodeReputation)});
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

        public async Task<bool> WasMessageReceived(string senderIdHash, MessageType messageType, int timeout)
        {
            var completionSource = GetCompletionSource(senderIdHash, (int)messageType);
            var firstTask = await Task.WhenAny(completionSource.Task, Task.Delay(timeout));
            return firstTask == completionSource.Task;
        }

        public event EventHandler<NodeEventArgs> NodeDiscovered;

        public IReadOnlyCollection<INodeLifecycleManager> GetOrAddNodeLifecycleManagers()
        {
            return _nodeLifecycleManagers.Values.ToArray();
        }

        public IReadOnlyCollection<INodeLifecycleManager> GetOrAddNodeLifecycleManagers(Func<INodeLifecycleManager, bool> query)
        {
            return _nodeLifecycleManagers.Values.Where(query.Invoke).ToArray();
        }

        protected void ValidatePingAddress(PingMessage message)
        {
            if (message.DestinationAddress == null || message.SourceAddress == null || message.FarAddress == null)
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Received ping message with empty address, message: {message}");
                }
            }

            if (!Bytes.AreEqual(_nodeTable.MasterNode.Address.Address.GetAddressBytes(), message.DestinationAddress?.Address.GetAddressBytes()))
            {
                //throw new NetworkingException($"Received message with inccorect destination adress, message: {message}");
            }

            if (_nodeTable.MasterNode.Port != message.DestinationAddress?.Port)
            {
//                throw new NetworkingException($"Received message with inccorect destination port, message: {message}");
            }

            if (!Bytes.AreEqual(message.FarAddress?.Address.GetAddressBytes(), message.SourceAddress?.Address.GetAddressBytes()))
            {
                //throw new NetworkingException($"Received message with inccorect source adress, message: {message}");
            }

            if (message.FarAddress?.Port != message.SourceAddress?.Port)
            {
                if (_logger.IsTrace)
                {
                    _logger.Warn($"Received message with incorect source port, message: {message}");
                }
            }
        }

        private void OnNewNode(INodeLifecycleManager manager)
        {
            NodeDiscovered?.Invoke(this, new NodeEventArgs(manager.ManagedNode, manager.NodeStats));
        }

        private void NotifySubscribersOnMsgReceived(MessageType msgType, Node node, DiscoveryMessage message)
        {
            var completionSource = RemoveCompletionSource(node.IdHashText, (int)msgType);
            completionSource?.TrySetResult(message);
        }

        private TaskCompletionSource<DiscoveryMessage> GetCompletionSource(string senderAddressHash, int messageType)
        {
            var key = new MessageTypeKey(senderAddressHash, messageType);
            var completionSource = _waitingEvents.GetOrAdd(key, new TaskCompletionSource<DiscoveryMessage>());
            return completionSource;
        }

        private TaskCompletionSource<DiscoveryMessage> RemoveCompletionSource(string senderAddressHash, int messageType)
        {
            var key = new MessageTypeKey(senderAddressHash, messageType);
            return _waitingEvents.TryRemove(key, out var completionSource) ? completionSource : null;
        }

        private void CleanUpLifecycleManagers()
        {
            if (_nodeLifecycleManagers.Count <= _configurationProvider.MaxNodeLifecycleManagersCount)
            {
                return;
            }

            int cleanupCount = _configurationProvider.NodeLifecycleManagersCleaupCount;
            var activeExcluded = _nodeLifecycleManagers.Where(x => x.Value.State == NodeLifecycleState.ActiveExcluded).Take(cleanupCount).ToArray();
            if (activeExcluded.Length == cleanupCount)
            {
                var removeCounter = RemoveManagers(activeExcluded, activeExcluded.Length);
                if(_logger.IsTrace) _logger.Trace($"Removed: {removeCounter} activeExcluded node lifecycle managers");
                return;
            }

            var unreachable = _nodeLifecycleManagers.Where(x => x.Value.State == NodeLifecycleState.Unreachable).Take(cleanupCount - activeExcluded.Length).ToArray();
            var removeCount = RemoveManagers(activeExcluded, activeExcluded.Length);
            removeCount = removeCount + RemoveManagers(unreachable, unreachable.Length);
            if(_logger.IsTrace) _logger.Trace($"Removed: {removeCount} unreachable node lifecycle managers");
        }

        private int RemoveManagers(KeyValuePair<string, INodeLifecycleManager>[] items, int count)
        {
            var removeCount = 0;
            for (var i = 0; i < count; i++)
            {
                var item = items[i];
                if (_nodeLifecycleManagers.TryRemove(item.Key, out _))
                {
                    _discoveryStorage.RemoveNodes(new[] { new NetworkNode(item.Value.ManagedNode.Id.PublicKey, item.Value.ManagedNode.Host, item.Value.ManagedNode.Port, item.Value.ManagedNode.Description, item.Value.NodeStats.NewPersistedNodeReputation),  });
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