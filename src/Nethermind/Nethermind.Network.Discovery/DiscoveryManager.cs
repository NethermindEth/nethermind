// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public class DiscoveryManager : IDiscoveryManager
{
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly RateLimiter _outgoingMessageRateLimiter;
    private readonly ILogger _logger;
    private readonly INodeLifecycleManagerFactory _nodeLifecycleManagerFactory;
    private readonly ConcurrentDictionary<Hash256, INodeLifecycleManager> _nodeLifecycleManagers = new();
    private readonly INodeTable _nodeTable;
    private readonly INetworkStorage _discoveryStorage;
    private readonly NodeFilter _nodesFilter;
    private readonly HashSet<IPAddress> _validDestinationAddresses;

    private readonly ConcurrentDictionary<MessageTypeKey, TaskCompletionSource<DiscoveryMsg>> _waitingEvents = new();
    private readonly EventHandler<NodeLifecycleState> _managerOnStateChanged;
    private readonly Func<Hash256, Node, INodeLifecycleManager> _createNodeLifecycleManager;
    private readonly Func<Hash256, Node, INodeLifecycleManager> _createPersistedNodeLifecycleManager;
    private IMsgSender? _msgSender;

    private const int MaxPendingSends = 512;
    private readonly Channel<DiscoveryMsg> _sendQueue = Channel.CreateBounded<DiscoveryMsg>(
        new BoundedChannelOptions(MaxPendingSends) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private int _sendQueueConsumerStarted;
    private int _sendQueueConsumersCreated;

    public DiscoveryManager(
        INodeLifecycleManagerFactory? nodeLifecycleManagerFactory,
        INodeTable? nodeTable,
        [KeyFilter(DbNames.DiscoveryNodes)] INetworkStorage? discoveryStorage,
        IDiscoveryConfig? discoveryConfig,
        INetworkConfig networkConfig,
        ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _discoveryConfig = discoveryConfig ?? throw new ArgumentNullException(nameof(discoveryConfig));
        _nodeLifecycleManagerFactory = nodeLifecycleManagerFactory ?? throw new ArgumentNullException(nameof(nodeLifecycleManagerFactory));
        _nodeTable = nodeTable ?? throw new ArgumentNullException(nameof(nodeTable));
        _discoveryStorage = discoveryStorage ?? throw new ArgumentNullException(nameof(discoveryStorage));
        _nodeLifecycleManagerFactory.DiscoveryManager = this;
        _managerOnStateChanged = ManagerOnOnStateChanged;
        _createNodeLifecycleManager = CreateNodeLifecycleManager;
        _createPersistedNodeLifecycleManager = CreatePersistedNodeLifecycleManager;
        _outgoingMessageRateLimiter = new RateLimiter(discoveryConfig.MaxOutgoingMessagePerSecond);
        IPAddress? currentIp = IPAddress.TryParse(networkConfig.ExternalIp ?? networkConfig.LocalIp, out IPAddress? ip) ? ip : null;
        int discoveryFilterTarget = Math.Max(networkConfig.MaxActivePeers, discoveryConfig.MaxNodeLifecycleManagersCount);
        _nodesFilter = NodeFilter.Create(discoveryFilterTarget, networkConfig.FilterDiscoveryNodesByRecentIp, networkConfig.FilterDiscoveryNodesBySameSubnet, currentIp);
        _validDestinationAddresses = GetValidDestinationAddresses(networkConfig, nodeTable);
    }

    public NodeRecord SelfNodeRecord => _nodeLifecycleManagerFactory.SelfNodeRecord;

    private INodeLifecycleManager CreateNodeLifecycleManager(Hash256 _, Node node) => CreateNodeLifecycleManager(node, isPersisted: false);

    private INodeLifecycleManager CreatePersistedNodeLifecycleManager(Hash256 _, Node node) => CreateNodeLifecycleManager(node, isPersisted: true);

    private INodeLifecycleManager CreateNodeLifecycleManager(Node node, bool isPersisted)
    {
        Interlocked.Increment(ref _managersCreated);
        INodeLifecycleManager manager = _nodeLifecycleManagerFactory.CreateNodeLifecycleManager(node);
        manager.OnStateChanged += _managerOnStateChanged;
        if (!isPersisted)
        {
            _discoveryStorage.UpdateNode(new NetworkNode(manager.ManagedNode.Id, manager.ManagedNode.Host, manager.ManagedNode.Port, manager.NodeStats.NewPersistedNodeReputation(DateTime.UtcNow)));
        }

        return manager;
    }

    public IMsgSender MsgSender
    {
        set => _msgSender = value;
    }

    public void OnIncomingMsg(DiscoveryMsg msg)
    {
        try
        {
            if (_logger.IsTrace) TraceMessage(msg);
            MsgType msgType = msg.MsgType;

            if (msgType == MsgType.Ping && !ValidatePingAddress((PingMsg)msg))
            {
                return;
            }

            Node node = new(msg.FarPublicKey, msg.FarAddress);
            INodeLifecycleManager? nodeManager = GetNodeLifecycleManager(node);
            if (nodeManager is null)
            {
                return;
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
                    nodeManager.ProcessPingMsg((PingMsg)msg);
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
                    LogErrorMessageType(msgType);
                    return;
            }

            NotifySubscribersOnMsgReceived(msgType, nodeManager.ManagedNode, msg);
            CleanUpLifecycleManagers();
        }
        catch (Exception e)
        {
            LogException(e);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceMessage(DiscoveryMsg msg) => _logger.Trace($"Received msg: {msg}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogErrorMessageType(MsgType msgType) => _logger.Error($"Unsupported msgType: {msgType}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogException(Exception e) => _logger.Error("Error during msg handling", e);
    }

    private int _managersCreated;

    public INodeLifecycleManager? GetNodeLifecycleManager(Node node, bool isPersisted = false, bool isTrusted = false)
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
            if (_logger.IsDebug) LogDebug(node);
            return null;
        }

        if (_nodeLifecycleManagers.TryGetValue(node.IdHash, out INodeLifecycleManager? existingManager))
        {
            _nodesFilter.Touch(node.Address.Address);
            return existingManager;
        }

        if (!isPersisted && !isTrusted && !_nodesFilter.TryAccept(node.Address.Address))
        {
            if (_logger.IsTrace) TraceNodeFilteredByRateLimit(node);
            return null;
        }

        return isPersisted
            ? _nodeLifecycleManagers.GetOrAdd(node.IdHash, _createPersistedNodeLifecycleManager, node)
            : _nodeLifecycleManagers.GetOrAdd(node.IdHash, _createNodeLifecycleManager, node);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceNodeFilteredByRateLimit(Node node) => _logger.Trace($"Node filtered from discovery manager, address: {node.Address}, id: {node.Id}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogDebug(Node node) => _logger.Debug($"Node is not listening - Port 0, blocking add to discovery, id: {node.Id}");
    }

    private void ManagerOnOnStateChanged(object? sender, NodeLifecycleState e)
    {
        if (e == NodeLifecycleState.Active)
        {
            if (sender is INodeLifecycleManager manager)
            {
                manager.OnStateChanged -= _managerOnStateChanged;
                NodeDiscovered?.Invoke(this, new NodeEventArgs(manager.ManagedNode));
            }
        }
    }

    public void SendMessage(DiscoveryMsg discoveryMsg)
    {
        // DropOldest: if queue is full, the oldest (stalest) message is evicted
        _sendQueue.Writer.TryWrite(discoveryMsg);
        EnsureSendQueueConsumerStarted();
    }

    private void EnsureSendQueueConsumerStarted()
    {
        if (Interlocked.CompareExchange(ref _sendQueueConsumerStarted, 1, 0) == 0)
        {
            Interlocked.Increment(ref _sendQueueConsumersCreated);
            _ = Task.Run(ProcessSendQueueAsync);
        }
    }

    private async Task ProcessSendQueueAsync()
    {
        await foreach (DiscoveryMsg msg in _sendQueue.Reader.ReadAllAsync())
        {
            await SendMessageAsync(msg);
        }
    }

    public async Task SendMessageAsync(DiscoveryMsg discoveryMsg)
    {
        if (_logger.IsTrace) LogTrace(discoveryMsg);
        try
        {
            if (_msgSender is null) return;
            await _outgoingMessageRateLimiter.WaitAsync(CancellationToken.None);
            await _msgSender.SendMsg(discoveryMsg);
        }
        catch (Exception e)
        {
            LogSendError(discoveryMsg, e);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogSendError(DiscoveryMsg discoveryMsg, Exception e)
            => _logger.Error($"Error during sending message: {discoveryMsg}", e);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogTrace(DiscoveryMsg discoveryMsg) => _logger.Trace($"Sending msg: {discoveryMsg}");
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> WasMessageReceived(Hash256 senderIdHash, MsgType msgType, int timeout, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<DiscoveryMsg> completionSource = GetCompletionSource(senderIdHash, (int)msgType);
        using CancellationTokenSource delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
            if (_logger.IsDebug) LogPingReceived(msg);
            return false;
        }

        if (!_validDestinationAddresses.Contains(msg.DestinationAddress.Address.MapToIPv6()))
        {
            if (_logger.IsDebug) LogUnexpectedPing(msg);
            return false;
        }

        return true;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogPingReceived(PingMsg msg) => _logger.Debug($"Received a ping message with empty address, message: {msg}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogUnexpectedPing(PingMsg msg) => _logger.Debug($"Received ping for unexpected destination address {msg.DestinationAddress.Address}, message: {msg}");
    }

    private static HashSet<IPAddress> GetValidDestinationAddresses(INetworkConfig networkConfig, INodeTable nodeTable)
    {
        HashSet<IPAddress> addresses = new();

        if (nodeTable.MasterNode?.Address.Address is { } masterAddress)
        {
            addresses.Add(masterAddress.MapToIPv6());
        }

        if (IPAddress.TryParse(networkConfig.ExternalIp, out IPAddress? externalIp))
        {
            addresses.Add(externalIp.MapToIPv6());
        }

        if (IPAddress.TryParse(networkConfig.LocalIp, out IPAddress? localIp))
        {
            addresses.Add(localIp.MapToIPv6());
        }

        return addresses;
    }

    private void NotifySubscribersOnMsgReceived(MsgType msgType, Node node, DiscoveryMsg msg)
    {
        TaskCompletionSource<DiscoveryMsg>? completionSource = RemoveCompletionSource(node.IdHash, (int)msgType);
        completionSource?.TrySetResult(msg);
    }

    private TaskCompletionSource<DiscoveryMsg> GetCompletionSource(Hash256 senderAddressHash, int messageType)
    {
        MessageTypeKey key = new(senderAddressHash, messageType);
        TaskCompletionSource<DiscoveryMsg> completionSource = _waitingEvents.GetOrAdd(key, new TaskCompletionSource<DiscoveryMsg>(TaskCreationOptions.RunContinuationsAsynchronously));
        return completionSource;
    }

    private TaskCompletionSource<DiscoveryMsg>? RemoveCompletionSource(Hash256 senderAddressHash, int messageType)
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
        RemoveByState(NodeLifecycleState.ActiveExcluded, ref remainingToRemove);
        if (remainingToRemove <= 0) { if (_logger.IsDebug) LogCleanup(toRemove); return; }

        RemoveByState(NodeLifecycleState.Unreachable, ref remainingToRemove);
        if (remainingToRemove <= 0) { if (_logger.IsDebug) LogCleanup(toRemove); return; }

        DateTime utcNow = DateTime.UtcNow;
        foreach ((Hash256 key, _) in _nodeLifecycleManagers.ToArray()
                     .OrderBy(x => x.Value.NodeStats.CurrentNodeReputation(utcNow)))
        {
            if (RemoveManager(key) && --remainingToRemove <= 0)
            {
                if (_logger.IsDebug) LogCleanup(toRemove);
                return;
            }
        }

        if (_logger.IsDebug) LogDebug(toRemove, remainingToRemove);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogDebug(int toRemove, int remainingToRemove)
            => _logger.Debug($"Cleaned up {toRemove - remainingToRemove} discovery lifecycle managers.");
    }

    private void RemoveByState(NodeLifecycleState state, ref int remainingToRemove)
    {
        foreach ((Hash256 key, INodeLifecycleManager value) in _nodeLifecycleManagers)
        {
            if (value.State == state && RemoveManager(key) && --remainingToRemove <= 0)
            {
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LogCleanup(int toRemove) => _logger.Debug($"Cleaned up {toRemove} discovery lifecycle managers.");

    private bool RemoveManager(Hash256 key)
    {
        if (_nodeLifecycleManagers.TryRemove(key, out INodeLifecycleManager? manager))
        {
            manager.OnStateChanged -= ManagerOnOnStateChanged;
            _discoveryStorage.RemoveNode(manager.ManagedNode.Id);
            return true;
        }

        return false;
    }

    public bool ShouldContact(IPAddress address)
        => _nodesFilter.CanAccept(address);

    internal int SendQueueConsumersCreated => Volatile.Read(ref _sendQueueConsumersCreated);

    private readonly struct MessageTypeKey(Hash256 senderAddressHash, int messageType) : IEquatable<MessageTypeKey>
    {
        public Hash256 SenderAddressHash { get; } = senderAddressHash;

        public int MessageType { get; } = messageType;

        public bool Equals(MessageTypeKey other)
        {
            return SenderAddressHash.Equals(other.SenderAddressHash) && MessageType == other.MessageType;
        }

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            return obj is MessageTypeKey key && Equals(key);
        }

        public readonly override int GetHashCode()
            => ((SenderAddressHash is not null ? SenderAddressHash.GetHashCode() : 0) * 397) ^ MessageType;
    }
}
