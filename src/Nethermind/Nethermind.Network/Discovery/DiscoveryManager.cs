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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        private readonly IIPResolver _ipResolver;

        private readonly ConcurrentDictionary<MessageTypeKey, TaskCompletionSource<DiscoveryMessage>> _waitingEvents = new ConcurrentDictionary<MessageTypeKey, TaskCompletionSource<DiscoveryMessage>>();
        private IMessageSender _messageSender;

        public DiscoveryManager(
            INodeLifecycleManagerFactory nodeLifecycleManagerFactory,
            INodeTable nodeTable,
            INetworkStorage discoveryStorage,
            IDiscoveryConfig discoveryConfig,
            ILogManager logManager,
            IIPResolver ipResolver)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
            _nodeLifecycleManagerFactory = nodeLifecycleManagerFactory ?? throw new ArgumentNullException(nameof(nodeLifecycleManagerFactory));
            _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
            _discoveryStorage = discoveryStorage ?? throw new ArgumentNullException(nameof(discoveryStorage));
            _nodeLifecycleManagerFactory.DiscoveryManager = this;
            _ipResolver = ipResolver;
        }

        public IMessageSender MessageSender
        {
            set => _messageSender = value;
        }

        public void OnIncomingMessage(DiscoveryMessage message)
        {
            try
            {
                if (_logger.IsTrace) _logger.Trace($"Received msg: {message}");
                MessageType msgType = message.MessageType;

                Node node = new Node(message.FarPublicKey, message.FarAddress);
                INodeLifecycleManager nodeManager = GetNodeLifecycleManager(node);
                if (nodeManager == null)
                {
                    return;
                }

                if (message is PingMessage pingMessage)
                {
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(pingMessage.FarAddress, "MANAGER disc v4", $"Ping {pingMessage.SourceAddress.Address} -> {pingMessage.DestinationAddress.Address}");
                }
                else
                {
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(message.FarAddress, "MANAGER disc v4", message.MessageType.ToString());
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
                        if (ValidatePingAddress(ping))
                        {
                            nodeManager.ProcessPingMessage(ping);
                        }
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

        private int _managersCreated = 0;

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
                Interlocked.Increment(ref _managersCreated);
                INodeLifecycleManager manager = _nodeLifecycleManagerFactory.CreateNodeLifecycleManager(node);
                if (!isPersisted)
                {
                    _discoveryStorage.UpdateNodes(new[] { new NetworkNode(manager.ManagedNode.Id, manager.ManagedNode.Host, manager.ManagedNode.Port, manager.NodeStats.NewPersistedNodeReputation) });
                }

                OnNewNode(manager);
                return manager;
            });
        }

        public void SendMessage(DiscoveryMessage discoveryMessage)
        {
            if (_logger.IsTrace) _logger.Trace($"Sending msg: {discoveryMessage}");
            try
            {
                if (discoveryMessage is PingMessage pingMessage)
                {
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportOutgoingMessage(pingMessage.FarAddress, "HANDLER disc v4", $"Ping {pingMessage.SourceAddress.Address} -> {pingMessage.DestinationAddress.Address}");
                }
                else
                {
                    if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportOutgoingMessage(discoveryMessage.FarAddress, "HANDLER disc v4", discoveryMessage.MessageType.ToString());
                }

                _messageSender.SendMessage(discoveryMessage);
            }
            catch (Exception e)
            {
                _logger.Error($"Error during sending message: {discoveryMessage}", e);
            }
        }

        public async Task<bool> WasMessageReceived(Keccak senderIdHash, MessageType messageType, int timeout)
        {
            TaskCompletionSource<DiscoveryMessage> completionSource = GetCompletionSource(senderIdHash, (int)messageType);
            CancellationTokenSource delayCancellation = new CancellationTokenSource();
            Task firstTask = await Task.WhenAny(completionSource.Task, Task.Delay(timeout, delayCancellation.Token));

            bool result = firstTask == completionSource.Task;
            if (result)
            {
                delayCancellation.Cancel();
            }
            else
            {
                RemoveCompletionSource(senderIdHash, (int)messageType);
            }

            return result;
        }

        public event EventHandler<NodeEventArgs> NodeDiscovered;

        public IReadOnlyCollection<INodeLifecycleManager> GetNodeLifecycleManagers()
        {
            return _nodeLifecycleManagers.Values.ToArray();
        }

        public IReadOnlyCollection<INodeLifecycleManager> GetOrAddNodeLifecycleManagers(Func<INodeLifecycleManager, bool> query)
        {
            return _nodeLifecycleManagers.Values.Where(query.Invoke).ToArray();
        }

        private bool ValidatePingAddress(PingMessage message)
        {
            if (message.DestinationAddress == null || message.FarAddress == null)
            {
                if (_logger.IsDebug) _logger.Debug($"Received a ping message with empty address, message: {message}");
                return false;
            }

            #region 
            // port will be different as we dynamically open ports for each socket connection
            // if (_nodeTable.MasterNode.Port != message.DestinationAddress?.Port)
            // {
            //     throw new NetworkingException($"Received message with incorrect destination port, message: {message}", NetworkExceptionType.Discovery);
            // }

            // either an old Nethermind or other nodes that make the same mistake 
            // if (!Bytes.AreEqual(message.FarAddress?.Address.MapToIPv6().GetAddressBytes(), message.SourceAddress?.Address.MapToIPv6().GetAddressBytes()))
            // {
            //     // there is no sense to complain here as nodes sent a lot of garbage as their source addresses
            //     // if (_logger.IsWarn) _logger.Warn($"Received message with incorrect source address {message.SourceAddress}, message: {message}");
            // }

            // if (message.FarAddress?.Port != message.SourceAddress?.Port)
            // {
            //     // there is no sense to complain here as nodes sent a lot of garbage as their source addresses
            //     // if (_logger.IsWarn) _logger.Warn($"TRACE/WARN Received a message with incorrect source port, message: {message}");
            // }
            #endregion

            return true;
        }

        private void OnNewNode(INodeLifecycleManager manager)
        {
            NodeDiscovered?.Invoke(this, new NodeEventArgs(manager.ManagedNode));
        }

        private void NotifySubscribersOnMsgReceived(MessageType msgType, Node node, DiscoveryMessage message)
        {
            TaskCompletionSource<DiscoveryMessage> completionSource = RemoveCompletionSource(node.IdHash, (int)msgType);
            completionSource?.TrySetResult(message);
        }

        private TaskCompletionSource<DiscoveryMessage> GetCompletionSource(Keccak senderAddressHash, int messageType)
        {
            MessageTypeKey key = new MessageTypeKey(senderAddressHash, messageType);
            TaskCompletionSource<DiscoveryMessage> completionSource = _waitingEvents.GetOrAdd(key, new TaskCompletionSource<DiscoveryMessage>());
            return completionSource;
        }

        private TaskCompletionSource<DiscoveryMessage> RemoveCompletionSource(Keccak senderAddressHash, int messageType)
        {
            MessageTypeKey key = new MessageTypeKey(senderAddressHash, messageType);
            return _waitingEvents.TryRemove(key, out TaskCompletionSource<DiscoveryMessage> completionSource) ? completionSource : null;
        }

        private void CleanUpLifecycleManagers()
        {
            int toRemove = (_nodeLifecycleManagers.Count - _discoveryConfig.MaxNodeLifecycleManagersCount) + _discoveryConfig.NodeLifecycleManagersCleanupCount;
            if(toRemove <= _discoveryConfig.NodeLifecycleManagersCleanupCount / 2)
            {
                return;
            }

            int remainingToRemove = toRemove;
            foreach ((Keccak key, INodeLifecycleManager value) in _nodeLifecycleManagers)
            {
                if (value.State == NodeLifecycleState.ActiveExcluded)
                {
                    if (RemoveManager((key, value.ManagedNode.Id)))
                    {
                        remainingToRemove--;
                        if (remainingToRemove <= 0)
                        {
                            if(_logger.IsDebug) _logger.Debug($"Cleaned up {toRemove} discovery lifecycle managers.");
                            return;
                        }
                    }
                }
            }

            foreach ((Keccak key, INodeLifecycleManager value) in _nodeLifecycleManagers)
            {
                if (value.State == NodeLifecycleState.Unreachable)
                {
                    if (RemoveManager((key, value.ManagedNode.Id)))
                    {
                        remainingToRemove--;
                        if (remainingToRemove <= 0)
                        {
                            if(_logger.IsDebug) _logger.Debug($"Cleaned up {toRemove} discovery lifecycle managers.");
                            return;
                        }
                    }
                }
            }

            foreach ((Keccak key, INodeLifecycleManager value) in _nodeLifecycleManagers.ToArray()
                .OrderBy(x => x.Value.NodeStats.CurrentNodeReputation))
            {
                if (RemoveManager((key, value.ManagedNode.Id)))
                {
                    remainingToRemove--;
                    if (remainingToRemove <= 0)
                    {
                        if(_logger.IsDebug) _logger.Debug($"Cleaned up {toRemove} discovery lifecycle managers.");
                        return;
                    }
                }
            }

            if(_logger.IsDebug) _logger.Debug($"Cleaned up {toRemove - remainingToRemove} discovery lifecycle managers.");
        }

        private bool RemoveManager((Keccak Hash, PublicKey Key) item)
        {
            if (_nodeLifecycleManagers.TryRemove(item.Hash, out _))
            {
                _discoveryStorage.RemoveNode(item.Key);
                return true;
            }

            return false;
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
