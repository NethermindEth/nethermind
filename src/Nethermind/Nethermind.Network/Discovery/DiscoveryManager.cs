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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery
{
    public class DiscoveryManager : IDiscoveryManager
    {
        private readonly IDiscoveryConfig _discoveryConfig;
        private readonly ILogger _logger;
        private readonly INodeLifecycleManagerFactory _nodeLifecycleManagerFactory;
        private readonly ConcurrentDictionary<Keccak, INodeLifecycleManager> _nodeLifecycleManagers = new ConcurrentDictionary<Keccak, INodeLifecycleManager>();
        private readonly INodeTable _nodeTable;
        private readonly INetworkStorage _discoveryStorage;

        private readonly ConcurrentDictionary<MessageTypeKey, TaskCompletionSource<DiscoveryMessage>> _waitingEvents = new ConcurrentDictionary<MessageTypeKey, TaskCompletionSource<DiscoveryMessage>>();
        private IMessageSender _messageSender;

        public DiscoveryManager(
            INodeLifecycleManagerFactory nodeLifecycleManagerFactory,
            INodeTable nodeTable,
            INetworkStorage discoveryStorage,
            IDiscoveryConfig discoveryConfig,
            ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
            _nodeLifecycleManagerFactory = nodeLifecycleManagerFactory ?? throw new ArgumentNullException(nameof(nodeLifecycleManagerFactory));
            _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
            _discoveryStorage = discoveryStorage ?? throw new ArgumentNullException(nameof(discoveryStorage));
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

                Node node = new Node(message.FarPublicKey, message.FarAddress);
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

            return _nodeLifecycleManagers.GetOrAdd(node.IdHash, x =>
            {
                var manager = _nodeLifecycleManagerFactory.CreateNodeLifecycleManager(node);
                if (!isPersisted)
                {
                    _discoveryStorage.UpdateNodes(new[] { new NetworkNode(manager.ManagedNode.Id, manager.ManagedNode.Host, manager.ManagedNode.Port, manager.NodeStats.NewPersistedNodeReputation)});
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

        public async Task<bool> WasMessageReceived(Keccak senderIdHash, MessageType messageType, int timeout)
        {
            var completionSource = GetCompletionSource(senderIdHash, (int)messageType);
            CancellationTokenSource delayCancellation = new CancellationTokenSource();
            var firstTask = await Task.WhenAny(completionSource.Task, Task.Delay(timeout, delayCancellation.Token));

            bool result = firstTask == completionSource.Task;
            if (result)
            {
                delayCancellation.Cancel();
            }
            
            return result;
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

        private void ValidatePingAddress(PingMessage message)
        {
            if (message.DestinationAddress == null || message.SourceAddress == null || message.FarAddress == null)
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Received a ping message with empty address, message: {message}");
                }
            }

            if (!Bytes.AreEqual(_nodeTable.MasterNode.Address.Address.GetAddressBytes(), message.DestinationAddress?.Address.GetAddressBytes()))
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Received a message with incorrect destination address, message: {message}");
                }
            }

            // port will be different as we dynamically open ports for each socket connection
//            if (_nodeTable.MasterNode.Port != message.DestinationAddress?.Port)
//            {
//                throw new NetworkingException($"Received message with incorrect destination port, message: {message}");
//            }

            // either an old Nethermind or other nodes that make the same mistake 
//            if (!Bytes.AreEqual(message.FarAddress?.Address.GetAddressBytes(), message.SourceAddress?.Address.GetAddressBytes()))
//            {
//                throw new NetworkingException($"Received message with incorrect source address, message: {message}", NetworkExceptionType.Discovery);
//            }

            if (message.FarAddress?.Port != message.SourceAddress?.Port)
            {
                if (_logger.IsTrace)
                {
                    _logger.Warn($"Received a message with incorect source port, message: {message}");
                }
            }
        }

        private void OnNewNode(INodeLifecycleManager manager)
        {
            NodeDiscovered?.Invoke(this, new NodeEventArgs(manager.ManagedNode));
        }

        private void NotifySubscribersOnMsgReceived(MessageType msgType, Node node, DiscoveryMessage message)
        {
            var completionSource = RemoveCompletionSource(node.IdHash, (int)msgType);
            completionSource?.TrySetResult(message);
        }

        private TaskCompletionSource<DiscoveryMessage> GetCompletionSource(Keccak senderAddressHash, int messageType)
        {
            var key = new MessageTypeKey(senderAddressHash, messageType);
            var completionSource = _waitingEvents.GetOrAdd(key, new TaskCompletionSource<DiscoveryMessage>());
            return completionSource;
        }

        private TaskCompletionSource<DiscoveryMessage> RemoveCompletionSource(Keccak senderAddressHash, int messageType)
        {
            var key = new MessageTypeKey(senderAddressHash, messageType);
            return _waitingEvents.TryRemove(key, out var completionSource) ? completionSource : null;
        }

        private void CleanUpLifecycleManagers()
        {
            if (_nodeLifecycleManagers.Count <= _discoveryConfig.MaxNodeLifecycleManagersCount)
            {
                return;
            }

            int cleanupCount = _discoveryConfig.NodeLifecycleManagersCleanupCount;
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

        private int RemoveManagers(KeyValuePair<Keccak, INodeLifecycleManager>[] items, int count)
        {
            var removeCount = 0;
            for (var i = 0; i < count; i++)
            {
                var item = items[i];
                if (_nodeLifecycleManagers.TryRemove(item.Key, out _))
                {
                    _discoveryStorage.RemoveNodes(new[] { new NetworkNode(item.Value.ManagedNode.Id, item.Value.ManagedNode.Host, item.Value.ManagedNode.Port, item.Value.NodeStats.NewPersistedNodeReputation),  });
                    removeCount++;
                }
            }

            return removeCount;
        }

        private struct MessageTypeKey : IEquatable<MessageTypeKey>
        {
            public Keccak SenderAddressHash { get; }
            public int MessageType { get; }

            public MessageTypeKey(Keccak senderAddressHash, int messageType)
            {
                SenderAddressHash = senderAddressHash;
                MessageType = messageType;
            }
            
            public bool Equals(MessageTypeKey other)
            {
                return SenderAddressHash.Equals(other.SenderAddressHash) && MessageType == other.MessageType;
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                return obj is MessageTypeKey key && Equals(key);
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