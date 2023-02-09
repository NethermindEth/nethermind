// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public class DiscoveryManager : IDiscoveryManager
{
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly ILogger _logger;
    private readonly INodeLifecycleManagerFactory _nodeLifecycleManagerFactory;
    private readonly ConcurrentDictionary<Keccak, INodeLifecycleManager> _nodeLifecycleManagers = new();
    private readonly INodeTable _nodeTable;
    private readonly INetworkStorage _discoveryStorage;

    private readonly ConcurrentDictionary<MessageTypeKey, TaskCompletionSource<DiscoveryMsg>> _waitingEvents = new();
    private IMsgSender? _msgSender;

    public DiscoveryManager(
        INodeLifecycleManagerFactory? nodeLifecycleManagerFactory,
        INodeTable? nodeTable,
        INetworkStorage? discoveryStorage,
        IDiscoveryConfig? discoveryConfig,
        ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
        _nodeLifecycleManagerFactory = nodeLifecycleManagerFactory ?? throw new ArgumentNullException(nameof(nodeLifecycleManagerFactory));
        _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
        _discoveryStorage = discoveryStorage ?? throw new ArgumentNullException(nameof(discoveryStorage));
        _nodeLifecycleManagerFactory.DiscoveryManager = this;
    }

    public IMsgSender MsgSender
    {
        set => _msgSender = value;
    }

    public void OnIncomingMsg(DiscoveryMsg msg)
    {
        try
        {
            if (_logger.IsTrace) _logger.Trace($"Received msg: {msg}");
            MsgType msgType = msg.MsgType;

            Node node = new(msg.FarPublicKey, msg.FarAddress);
            INodeLifecycleManager? nodeManager = GetNodeLifecycleManager(node);
            if (nodeManager is null)
            {
                return;
            }

            if (msg is PingMsg pingMsg)
            {
                if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(pingMsg.FarAddress, "MANAGER disc v4", $"Ping {pingMsg.SourceAddress?.Address} -> {pingMsg.DestinationAddress?.Address}");
            }
            else
            {
                if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "MANAGER disc v4", msg.MsgType.ToString());
            }

            switch (msgType)
            {
                case MsgType.Neighbors:
                    nodeManager.ProcessNeighborsMsg((NeighborsMsg)msg);
                    break;
                case MsgType.Pong:
                    nodeManager.ProcessPongMsg((PongMsg)msg);
                    break;
                case MsgType.Ping:
                    PingMsg ping = (PingMsg)msg;
                    if (ValidatePingAddress(ping))
                    {
                        nodeManager.ProcessPingMsg(ping);
                    }
                    break;
                case MsgType.FindNode:
                    nodeManager.ProcessFindNodeMsg((FindNodeMsg)msg);
                    break;
                case MsgType.EnrRequest:
                    nodeManager.ProcessEnrRequestMsg((EnrRequestMsg)msg);
                    break;
                case MsgType.EnrResponse:
                    nodeManager.ProcessEnrResponseMsg((EnrResponseMsg)msg);
                    break;
                default:
                    _logger.Error($"Unsupported msgType: {msgType}");
                    return;
            }

            NotifySubscribersOnMsgReceived(msgType, nodeManager.ManagedNode, msg);
            CleanUpLifecycleManagers();
        }
        catch (Exception e)
        {
            _logger.Error("Error during msg handling", e);
        }
    }

    private int _managersCreated;

    public INodeLifecycleManager? GetNodeLifecycleManager(Node node, bool isPersisted = false)
    {
        if (_nodeTable.MasterNode is null)
        {
            return null;
        }

        if (_nodeTable.MasterNode.Equals(node))
        {
            return null;
        }

        if (node.Port == 0)
        {
            if (_logger.IsDebug) _logger.Debug($"Node is not listening - Port 0, blocking add to discovery, id: {node.Id}");
            return null;
        }

        return _nodeLifecycleManagers.GetOrAdd(node.IdHash, _ =>
        {
            Interlocked.Increment(ref _managersCreated);
            INodeLifecycleManager manager = _nodeLifecycleManagerFactory.CreateNodeLifecycleManager(node);
            manager.OnStateChanged += ManagerOnOnStateChanged;
            if (!isPersisted)
            {
                _discoveryStorage.UpdateNodes(new[] { new NetworkNode(manager.ManagedNode.Id, manager.ManagedNode.Host, manager.ManagedNode.Port, manager.NodeStats.NewPersistedNodeReputation) });
            }

            return manager;
        });
    }

    private void ManagerOnOnStateChanged(object? sender, NodeLifecycleState e)
    {
        if (e == NodeLifecycleState.Active)
        {
            if (sender is INodeLifecycleManager manager)
            {
                manager.OnStateChanged -= ManagerOnOnStateChanged;
                NodeDiscovered?.Invoke(this, new NodeEventArgs(manager.ManagedNode));
            }
        }
    }

    public void SendMessage(DiscoveryMsg discoveryMsg)
    {
        if (_logger.IsTrace) _logger.Trace($"Sending msg: {discoveryMsg}");
        try
        {
            if (discoveryMsg is PingMsg pingMessage)
            {
                if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportOutgoingMessage(pingMessage.FarAddress, "HANDLER disc v4", $"Ping {pingMessage.SourceAddress?.Address} -> {pingMessage.DestinationAddress?.Address}");
            }
            else
            {
                if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportOutgoingMessage(discoveryMsg.FarAddress, "HANDLER disc v4", discoveryMsg.MsgType.ToString());
            }

            _msgSender?.SendMsg(discoveryMsg);
        }
        catch (Exception e)
        {
            _logger.Error($"Error during sending message: {discoveryMsg}", e);
        }
    }

    public async Task<bool> WasMessageReceived(Keccak senderIdHash, MsgType msgType, int timeout)
    {
        TaskCompletionSource<DiscoveryMsg> completionSource = GetCompletionSource(senderIdHash, (int)msgType);
        CancellationTokenSource delayCancellation = new();
        Task firstTask = await Task.WhenAny(completionSource.Task, Task.Delay(timeout, delayCancellation.Token));

        bool result = firstTask == completionSource.Task;
        if (result)
        {
            delayCancellation.Cancel();
        }
        else
        {
            RemoveCompletionSource(senderIdHash, (int)msgType);
        }

        return result;
    }

    public event EventHandler<NodeEventArgs>? NodeDiscovered;

    public IReadOnlyCollection<INodeLifecycleManager> GetNodeLifecycleManagers()
    {
        return _nodeLifecycleManagers.Values.ToArray();
    }

    public IReadOnlyCollection<INodeLifecycleManager> GetOrAddNodeLifecycleManagers(Func<INodeLifecycleManager, bool> query)
    {
        return _nodeLifecycleManagers.Values.Where(query.Invoke).ToArray();
    }

    private bool ValidatePingAddress(PingMsg msg)
    {
        if (msg.DestinationAddress is null || msg.FarAddress is null)
        {
            if (_logger.IsDebug) _logger.Debug($"Received a ping message with empty address, message: {msg}");
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

    private void NotifySubscribersOnMsgReceived(MsgType msgType, Node node, DiscoveryMsg msg)
    {
        TaskCompletionSource<DiscoveryMsg>? completionSource = RemoveCompletionSource(node.IdHash, (int)msgType);
        completionSource?.TrySetResult(msg);
    }

    private TaskCompletionSource<DiscoveryMsg> GetCompletionSource(Keccak senderAddressHash, int messageType)
    {
        MessageTypeKey key = new(senderAddressHash, messageType);
        TaskCompletionSource<DiscoveryMsg> completionSource = _waitingEvents.GetOrAdd(key, new TaskCompletionSource<DiscoveryMsg>());
        return completionSource;
    }

    private TaskCompletionSource<DiscoveryMsg>? RemoveCompletionSource(Keccak senderAddressHash, int messageType)
    {
        MessageTypeKey key = new(senderAddressHash, messageType);
        return _waitingEvents.TryRemove(key, out TaskCompletionSource<DiscoveryMsg>? completionSource) ? completionSource : null;
    }

    private void CleanUpLifecycleManagers()
    {
        int toRemove = (_nodeLifecycleManagers.Count - _discoveryConfig.MaxNodeLifecycleManagersCount) + _discoveryConfig.NodeLifecycleManagersCleanupCount;
        if (toRemove <= _discoveryConfig.NodeLifecycleManagersCleanupCount / 2)
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
                        if (_logger.IsDebug) _logger.Debug($"Cleaned up {toRemove} discovery lifecycle managers.");
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
                        if (_logger.IsDebug) _logger.Debug($"Cleaned up {toRemove} discovery lifecycle managers.");
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
                    if (_logger.IsDebug) _logger.Debug($"Cleaned up {toRemove} discovery lifecycle managers.");
                    return;
                }
            }
        }

        if (_logger.IsDebug) _logger.Debug($"Cleaned up {toRemove - remainingToRemove} discovery lifecycle managers.");
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

    private readonly struct MessageTypeKey : IEquatable<MessageTypeKey>
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

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            return obj is MessageTypeKey key && Equals(key);
        }

        [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
        public override int GetHashCode()
        {
            unchecked
            {
                return ((SenderAddressHash is not null ? SenderAddressHash.GetHashCode() : 0) * 397) ^ MessageType;
            }
        }
    }
}
