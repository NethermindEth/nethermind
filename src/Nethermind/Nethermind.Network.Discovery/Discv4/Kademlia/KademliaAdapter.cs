// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NonBlocking;

namespace Nethermind.Network.Discovery.Discv4.Kademlia;

public class KademliaAdapter(
    Lazy<IKademlia<PublicKey, Node>> kademlia, // Cyclic dependency
    IRoutingTable<Node, ValueHash256> routingTable,
    Lazy<INodeHealthTracker<Node>> nodeHealthTracker,
    IDiscoveryConfig discoveryConfig,
    KademliaConfig<Node> kademliaConfig,
    INodeRecordProvider nodeRecordProvider,
    IIPResolver ipResolver,
    INodeStatsManager nodeStatsManager,
    ITimestamper timestamper,
    IProcessExitSource processExitSource,
    IEcdsa ecdsa,
    ILogManager logManager,
    NetworkListenerState listenerState,
    PeerAddressPolicy addressPolicy,
    TimeProvider? timeProvider = null
) : KademliaAdapterBase("discv4", logManager.GetClassLogger<KademliaAdapter>(), listenerState), IKademliaAdapter
{

    private const int MaxNodesPerNeighborsMsg = 12;
    private const int PeerCandidateChannelCapacity = 64;

    internal DiscoveryRequestActivity RequestActivity { get; set; } = new();

    private readonly TimeSpan _requestEnrTimeout = TimeSpan.FromMilliseconds(discoveryConfig.EnrTimeout);
    private readonly TimeSpan _findNeighbourTimeout = TimeSpan.FromMilliseconds(discoveryConfig.SendNodeTimeout);
    private readonly TimeSpan _pingTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout);
    private readonly TimeSpan _expirationTime = TimeSpan.FromMilliseconds(discoveryConfig.MessageExpiryTime);
    private readonly TimeSpan _waitAfterPongDelay = TimeSpan.FromMilliseconds(discoveryConfig.BondWaitTime);

    private readonly RateLimiter _outboundRateLimiter = new(OutgoingRates.Split(discoveryConfig.MaxOutgoingMessagePerSecond).Requests, timeProvider);
    private readonly RateLimiter _responseRateLimiter = new(OutgoingRates.Split(discoveryConfig.MaxOutgoingMessagePerSecond).Responses, timeProvider);
    private readonly RateLimiter _inboundBondRateLimiter = new(OutgoingRates.Split(discoveryConfig.MaxOutgoingMessagePerSecond).InboundBonds, timeProvider);
    private readonly NodeRecordSigner _nodeRecordSigner = new(ecdsa);
    private readonly RecentNodeFilter<Hash256> _recentPeerCandidates = new(
        RecentNodeFilter.GetLimit(kademliaConfig.KSize, ValueHash256KademliaDistance.Instance.MaxDistance, PeerCandidateChannelCapacity));
    private readonly Channel<Node> _peerCandidates = Channel.CreateBounded<Node>(new BoundedChannelOptions(PeerCandidateChannelCapacity)
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<(ValueHash256, MsgType), IMessageHandler[]> _incomingMessageHandlers = new();
    private readonly LruCache<ValueHash256, NodeSession> _sessions = new(Math.Max(1, discoveryConfig.MaxNodeLifecycleManagersCount - ProbationCapacity(discoveryConfig.MaxNodeLifecycleManagersCount)), "bonded_node_sessions");
    private readonly LruCache<ValueHash256, NodeSession> _probationarySessions = new(ProbationCapacity(discoveryConfig.MaxNodeLifecycleManagersCount), "unbonded_node_sessions");
    private readonly Lock _sessionAdmissionLock = new();
    internal int RetainedSessionCount { get { lock (_sessionAdmissionLock) return _sessions.Count + _probationarySessions.Count; } }
    private static int ProbationCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2, nameof(IDiscoveryConfig.MaxNodeLifecycleManagersCount));
        return Math.Clamp(capacity / 4, 1, 1024);
    }
    private readonly IIPResolver _ipResolver = ipResolver;
    private readonly PeerAddressPolicy _addressPolicy = addressPolicy;
    private readonly EndpointCheckLimiter _endpointChecks = new(256);
    private readonly Node _currentNode = kademliaConfig.CurrentNodeId;

    public IMsgSender? MsgSender { get; set; }

    internal TimeProvider RequestTimeProvider { get; init; } = TimeProvider.System;

    protected override bool TryCreateNodeFromEnr(Node currentNode, NodeRecord record, [NotNullWhen(true)] out Node? refreshedNode)
    {
        IPEndPoint bonded = currentNode.DiscoveryAddress;
        if (!CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(record, LocalIp, bonded, out Node? advertised,
            currentNode.IsStatic || currentNode.IsTrusted || currentNode.IsBootnode ? PeerAddressPermissions.All : _addressPolicy.AllowedAddresses))
        {
            refreshedNode = null;
            return false;
        }

        bool matches = bonded.Port == advertised.DiscoveryPort && HasSameAddressBytes(bonded.Address, advertised.DiscoveryAddress.Address);
        refreshedNode = matches ? new Node(currentNode.Id, new IPEndPoint(bonded.Address, advertised.Port), bonded.Port) : advertised;
        return true;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<Node> ReadPeerCandidates(CancellationToken token)
        => _peerCandidates.Reader.ReadAllAsync(token);

    public NodeSession GetSession(Node node)
    {
        lock (_sessionAdmissionLock)
        {
            ValueHash256 hash = node.IdHash.ValueHash256;
            if (_sessions.TryGet(hash, out NodeSession? bonded)) return bonded;
            NodeSession session = _probationarySessions.SetOrGet(hash, (node, nodeStatsManager, timestamper),
                static (_, state) => new NodeSession(state.node, state.nodeStatsManager, state.timestamper));
            if (session.HasEndpointBond(node.DiscoveryAddress)) PromoteSession(node, session);
            return session;
        }
    }

    private void PromoteSession(Node node, NodeSession session)
    {
        lock (_sessionAdmissionLock)
        {
            _sessions.Set(node.IdHash.ValueHash256, session);
            _probationarySessions.Delete(node.IdHash.ValueHash256);
        }
    }

    private async Task<bool> EnsureOutgoingMessageBondedPeer(Node node, NodeSession nodeSession, CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;
        IPEndPoint endpoint = node.DiscoveryAddress;
        if (nodeSession.NotTooManyFailure && nodeSession.HasReceivedPingFrom(endpoint)) return true;

        if (Logger.IsTrace) TraceEnsureSession(node);
        PongMsg? pong = await TryBond(node, nodeSession, token);
        if (pong is null) return false;
        // Allow the remote peer's reciprocal ping/pong before sending an authenticated request.
        Task bondDelay = Task.Delay(_waitAfterPongDelay, RequestTimeProvider, token);
        await bondDelay.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (token.IsCancellationRequested) return false;

        await RefreshRemoteRecordIfNewer(node, pong.EnrSequence, token);
        if (token.IsCancellationRequested) return false;
        PublishNode(node, nodeSession, signedPing: null, pong.EnrSequence);

        if (Logger.IsTrace) TracePongSent(node);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceEnsureSession(Node node) => Logger.Trace($"Ensure session for node {node:hide}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TracePongSent(Node node) => Logger.Trace($"Node {node:hide} pong sent.");

    private async Task<DiscoveryResponse<T>> RunAuthenticatedRequest<T, TState>(Node node, NodeSession session, TState state, Func<TState, CancellationToken, Task<DiscoveryResponse<T>>> callRequest, CancellationToken token)
    {
        if (!await EnsureOutgoingMessageBondedPeer(node, session, token))
        {
            if (!token.IsCancellationRequested) session.OnAuthenticatedRequestFailure();
            return DiscoveryResponse<T>.None;
        }

        DiscoveryResponse<T> resp = await callRequest(state, token);
        if (resp.HasResponse)
        {
            session.ResetAuthenticatedRequestFailure();
        }
        else if (!token.IsCancellationRequested)
        {
            session.OnAuthenticatedRequestFailure();
        }

        return resp;
    }

    private void AddMessageHandler(
        MsgType msgType, ValueHash256 nodeId, IMessageHandler handler) => _incomingMessageHandlers.AddOrUpdate(
            (nodeId, msgType),
            static (_, handler) => [handler],
            static (_, currentHandler, handler) => [.. currentHandler, handler],
            handler
        );

    private void RemoveMessageHandler(
        MsgType msgType, ValueHash256 nodeId, IMessageHandler handler)
    {
        (ValueHash256 nodeId, MsgType msgType) key = (nodeId, msgType);

        while (true)
        {
            if (!_incomingMessageHandlers.TryGetValue(key, out IMessageHandler[]? current)) return;

            int newLength = 0;
            for (int i = 0; i < current.Length; i++)
            {
                if (!ReferenceEquals(current[i], handler)) newLength++;
            }

            if (newLength == current.Length) return;

            if (newLength == 0)
            {
                if (_incomingMessageHandlers.TryRemove(new KeyValuePair<(ValueHash256, MsgType), IMessageHandler[]>(key, current))) return;
                continue;
            }

            IMessageHandler[] newValue = new IMessageHandler[newLength];
            int newIndex = 0;
            for (int i = 0; i < current.Length; i++)
            {
                IMessageHandler currentHandler = current[i];
                if (!ReferenceEquals(currentHandler, handler))
                {
                    newValue[newIndex++] = currentHandler;
                }
            }

            if (_incomingMessageHandlers.TryUpdate(key, newValue, current)) return;
        }
    }

    private async Task<DiscoveryResponse<T>> CallAndWaitForResponse<T>(
        MsgType msgType,
        ITaskCompleter<T> messageHandler,
        Node receiver,
        NodeSession session,
        DiscoveryMsg msg,
        TimeSpan timeout,
        CancellationToken token,
        bool requireExactEndpoint = false,
        bool isInboundBond = false
    )
    {
        long onlineGeneration = RequestActivity.BeginRequest();
        using CancellationTokenSource timeoutCts = new(timeout, RequestTimeProvider);
        using CancellationTokenSource sendCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
        using CancellationTokenRegistration cancelRegistration = token.Register(
            static state => ((TaskCompletionSource<DiscoveryResponse<T>>)state!).TrySetResult(DiscoveryResponse<T>.None),
            messageHandler.TaskCompletionSource);
        using CancellationTokenRegistration timeoutRegistration = timeoutCts.Token.Register(
            static state => ((TaskCompletionSource<DiscoveryResponse<T>>)state!).TrySetResult(DiscoveryResponse<T>.None),
            messageHandler.TaskCompletionSource);

        EndpointBoundHandler endpointHandler = new(receiver, messageHandler, requireExactEndpoint);
        AddMessageHandler(msgType, receiver.IdHash, endpointHandler);
        try
        {
            bool sent = false;
            try
            {
                sent = await SendMessage(session, msg, sendCts.Token, maximumDelay: timeout / 2, isInboundBond: isInboundBond);
            }
            catch (OperationCanceledException) when (sendCts.IsCancellationRequested)
            {
            }

            if (!sent)
            {
                if (token.IsCancellationRequested) return DiscoveryResponse<T>.None;
                ThrowRequestNotSent();
            }

            DiscoveryResponse<T> response = await messageHandler.TaskCompletionSource.Task;
            if (response.HasResponse) RequestActivity.Answered(onlineGeneration);
            return response;
        }
        finally
        {
            RemoveMessageHandler(msgType, receiver.IdHash, endpointHandler);
        }
    }

    private sealed class EndpointBoundHandler : IMessageHandler
    {
        private readonly IMessageHandler _inner;
        private readonly bool _requireExactEndpoint;
        public Node RequestedNode { get; }

        public EndpointBoundHandler(Node receiver, IMessageHandler inner, bool requireExactEndpoint)
        {
            IPEndPoint endpoint = receiver.DiscoveryAddress;
            RequestedNode = new Node(receiver.Id, new IPEndPoint(endpoint.Address, receiver.Port), endpoint.Port)
            {
                IsStatic = receiver.IsStatic,
                IsTrusted = receiver.IsTrusted,
                IsBootnode = receiver.IsBootnode
            };
            RequestedNode.MergeEnrStateFrom(receiver);
            _inner = inner;
            _requireExactEndpoint = requireExactEndpoint;
        }

        public bool Handle(DiscoveryMsg message)
            // Ordinary replies tolerate NAT port changes; refreshed ENRs require proof of their exact endpoint.
            => message.FarAddress is { } source
                && source.Address.NormalizeMappedIPv4().Equals(RequestedNode.DiscoveryAddress.Address.NormalizeMappedIPv4())
                && (!_requireExactEndpoint || source.Port == RequestedNode.DiscoveryPort)
                && _inner.Handle(message);
    }

    private async Task<bool> SendMessage(NodeSession session, DiscoveryMsg msg, CancellationToken token, bool isResponse = false, TimeSpan? maximumDelay = null, bool isInboundBond = false)
    {
        if (MsgSender is { } sender)
        {
            if (token.IsCancellationRequested) return false;
            long remainingSeconds = msg.ExpirationTime - timestamper.UnixTime.SecondsLong;
            if (remainingSeconds <= 0) return false;
            TimeSpan backlog = TimeSpan.FromSeconds(Math.Min(1, remainingSeconds / 2d));
            if (maximumDelay is { } requestBudget && requestBudget < backlog) backlog = requestBudget;
            RateLimiter limiter = isResponse ? _responseRateLimiter : isInboundBond ? _inboundBondRateLimiter : _outboundRateLimiter;
            if (!limiter.TryReserve(backlog, out TimeSpan delay)) return false;

            Task ready = Task.Delay(delay, RequestTimeProvider, token);
            await ready.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (token.IsCancellationRequested || msg.ExpirationTime <= timestamper.UnixTime.SecondsLong) return false;
            await ready;

            await sender.SendMsg(msg);
            session.RecordStatsForOutgoingMsg(msg);
            return true;
        }
        return false;
    }

    private long CalculateExpirationTime() => (long)(_expirationTime.TotalSeconds + timestamper.UnixTime.SecondsLong);

    public async Task<bool> Ping(Node receiver, CancellationToken token)
    {
        bool result = await PingCore(receiver, token);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<bool> PingCore(Node receiver, CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;
        NodeSession session = GetSession(receiver);
        PongMsg? pong = await TryBond(receiver, session, token);
        if (pong is null)
        {
            return false;
        }

        await RefreshRemoteRecordIfNewer(receiver, pong.EnrSequence, token);
        if (token.IsCancellationRequested) return false;
        PublishNode(receiver, session, signedPing: null, pong.EnrSequence);
        return true;
    }

    private async Task<PongMsg?> TryBond(Node receiver, NodeSession session, CancellationToken token, bool requireExactEndpoint = false, bool isInboundBond = false)
    {
        IPEndPoint endpoint = receiver.DiscoveryAddress;
        long pingToken;
        if (!isInboundBond)
            pingToken = session.OnPingSent(endpoint);
        else if (!session.TryOnFirstPingSent(endpoint, out pingToken))
            return null;
        try
        {
            AddressFamily family = DiscoveryAddressSupport.GetFamily(endpoint.Address);
            IIPResolver.NethermindIp resolvedIp = await _ipResolver.Resolve(token);
            if (!TryGetSourceAddress(family, resolvedIp, out IPEndPoint? sourceAddress))
            {
                ThrowRequestNotSent();
            }

            PingMsg msg = new(endpoint, CalculateExpirationTime(), sourceAddress, GetSourceTcpPort(family), 0)
            {
                EnrSequence = (await nodeRecordProvider.GetCurrentAsync(token)).EnrSequence // optional and does not seem to be used anywhere.
            };
            DiscoveryResponse<PongMsg> response = await CallAndWaitForResponse(MsgType.Pong, new PongMsgHandler(msg), receiver, session, msg, _pingTimeout, token, requireExactEndpoint, isInboundBond);
            if (!response.HasResponse) return null;
            if (response.Value.FarAddress is not { } pongEndpoint) return null;
            // A pong whose port a NAT remapped answers the ping, but proves neither endpoint, so it does not bond.
            if (pongEndpoint.Port != endpoint.Port) return null;

            session.OnPongReceived(pongEndpoint);
            if (!session.HasEndpointBond(endpoint)) return null;

            PromoteSession(receiver, session);
            session.RecordStatsForIncomingMsg(response.Value);
            receiver.ValidatedProtocol ??= false;
            DiscoveryLiveness.Mark(receiver);
            return response.Value;
        }
        finally
        {
            session.OnPingCompleted(endpoint, pingToken);
        }
    }

    private bool TryGetSourceAddress(
        AddressFamily family,
        IIPResolver.NethermindIp resolvedIp,
        [NotNullWhen(true)] out IPEndPoint? sourceAddress)
    {
        Node currentNode = kademliaConfig.CurrentNodeId;
        if (ListenerState.DiscoveryAddress is not { } listenerAddress ||
            !DiscoveryAddressSupport.SupportsFamily(listenerAddress, family))
        {
            sourceAddress = null;
            return false;
        }

        IPAddress? sourceIp = family switch
        {
            AddressFamily.InterNetwork => resolvedIp.ExternalIpV4,
            AddressFamily.InterNetworkV6 => resolvedIp.ExternalIpV6,
            _ => null
        };

        if (sourceIp is null &&
            !listenerAddress.IsWildcardOrNone &&
            DiscoveryAddressSupport.GetFamily(listenerAddress) == family)
        {
            sourceIp = listenerAddress.NormalizeMappedIPv4();
        }

        // Discv4 recipients use the UDP envelope source; retain the bound family when no advertised address is known.
        sourceIp ??= family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
        sourceAddress = new IPEndPoint(sourceIp, currentNode.DiscoveryPort);
        return true;
    }

    private int GetSourceTcpPort(AddressFamily family)
        => ListenerState.RlpxAddress is { } rlpxAddress && DiscoveryAddressSupport.SupportsFamily(rlpxAddress, family)
            ? kademliaConfig.CurrentNodeId.Port
            : 0;

    public async Task<Node[]?> FindNeighbours(Node receiver, PublicKey target, CancellationToken token)
    {
        NodeSession session = GetSession(receiver);
        DiscoveryResponse<Node[]> response = await RunAuthenticatedRequest(receiver, session,
            (Adapter: this, Receiver: receiver, Session: session, Target: target, BucketSize: discoveryConfig.BucketSize), static (state, token) =>
        {
            KademliaAdapter adapter = state.Adapter;
            FindNodeMsg msg = new(state.Receiver.DiscoveryAddress, adapter.CalculateExpirationTime(), state.Target.Bytes);

            return adapter.CallAndWaitForResponse(MsgType.Neighbors, new NeighbourMsgHandler(state.BucketSize, adapter.LocalIp,
                adapter._addressPolicy.AllowedAddresses)
            {
                LocalNode = adapter._currentNode
            }, state.Receiver, state.Session, msg, adapter._findNeighbourTimeout, token);
        }, token);

        token.ThrowIfCancellationRequested();
        return response.HasResponse ? response.Value : null;
    }

    protected virtual Task RefreshRemoteRecordIfNewer(Node node, ulong? advertisedSequence, CancellationToken token)
        => advertisedSequence is { } sequence
            ? base.RefreshRemoteRecordIfNewer(node, sequence, token)
            : Task.CompletedTask;

    protected override async ValueTask<NodeRecord?> RequestRemoteRecord(Node node, ulong requestedSequence, CancellationToken token)
    {
        EnrResponseMsg? response = await SendEnrRequestCore(node, token);
        return response?.NodeRecord;
    }

    protected override bool IsEnrValidForNode(Node node, NodeRecord record)
        => HasExpectedNodeId(record, node.Id.Hash.ValueHash256);

    protected override void AddOrRefreshRemoteNode(Node node)
        => kademlia.Value.AddOrRefresh(node);

    protected override async ValueTask<bool> VerifyRemoteEndpoint(Node node, CancellationToken token)
    {
        NodeSession session = GetSession(node);
        if (!session.HasEndpointBond(node.DiscoveryAddress))
        {
            if (!_endpointChecks.TryReserve(node)) return false;
            try
            {
                if (await TryBond(node, session, token, requireExactEndpoint: true) is null) return false;
            }
            finally
            {
                _endpointChecks.Release(node);
            }
        }
        token.ThrowIfCancellationRequested();
        node.ValidatedProtocol ??= false;
        DiscoveryLiveness.Mark(node);
        return true;
    }

    private void MergeKnownEnrState(Node node)
    {
        if (routingTable.TryGet(node.Id.Hash.ValueHash256, out Node? knownNode))
        {
            node.IsStatic |= knownNode.IsStatic;
            node.IsTrusted |= knownNode.IsTrusted;
            node.IsBootnode |= knownNode.IsBootnode;
            node.MergeEnrStateFrom(knownNode);
        }
    }

    public async Task<EnrResponseMsg?> SendEnrRequest(Node receiver, CancellationToken token)
    {
        EnrResponseMsg? response = await SendEnrRequestCore(receiver, token);
        token.ThrowIfCancellationRequested();
        return response;
    }

    private async Task<EnrResponseMsg?> SendEnrRequestCore(Node receiver, CancellationToken token)
    {
        NodeSession session = GetSession(receiver);
        DiscoveryResponse<EnrResponseMsg> response = await RunAuthenticatedRequest(receiver, session,
            (Adapter: this, Receiver: receiver, Session: session), static (state, token) =>
        {
            KademliaAdapter adapter = state.Adapter;
            EnrRequestMsg msg = new(state.Receiver.DiscoveryAddress, adapter.CalculateExpirationTime());

            return adapter.CallAndWaitForResponse(MsgType.EnrResponse, new EnrResponseHandler(msg), state.Receiver, state.Session, msg, adapter._requestEnrTimeout, token);
        }, token);

        return response.HasResponse ? response.Value : null;
    }

    private async Task<bool> HandleEnrRequest(Node node, NodeSession session, EnrRequestMsg msg, CancellationToken token)
    {
        IPEndPoint endpoint = node.DiscoveryAddress;
        await WaitForPendingEndpointBond(endpoint, session, token);

        if (!session.HasEndpointBond(endpoint))
        {
            if (Logger.IsTrace) TraceUnbondedEnrRequest(endpoint, node);
            return false;
        }

        if (msg.Hash is not { } requestHash)
        {
            if (Logger.IsTrace) TraceEnrRequestWithoutHash(node);
            return false;
        }

        await SendMessage(session, new EnrResponseMsg(endpoint, await nodeRecordProvider.GetCurrentAsync(token), new Hash256(requestHash)), token, isResponse: true);
        return true;
    }

    private async Task<bool> HandleFindNode(Node node, NodeSession session, FindNodeMsg msg, CancellationToken token)
    {
        IPEndPoint endpoint = node.DiscoveryAddress;
        await WaitForPendingEndpointBond(endpoint, session, token);

        if (!session.HasEndpointBond(endpoint))
        {
            if (Logger.IsTrace) TraceUnbondedFindNodeRequest(endpoint, node);
            return false;
        }

        PublicKey publicKey = new(msg.SearchedNodeId);
        Node[] nodes = kademlia.Value.GetKNeighbour(publicKey, node, false);
        if (nodes.Length == 0)
        {
            await SendMessage(session, new NeighborsMsg(endpoint, CalculateExpirationTime(), nodes), token, isResponse: true);
            return true;
        }

        for (int i = 0; i < nodes.Length; i += MaxNodesPerNeighborsMsg)
        {
            int batchEnd = Math.Min(i + MaxNodesPerNeighborsMsg, nodes.Length);
            await SendMessage(session, new NeighborsMsg(endpoint, CalculateExpirationTime(), new ArraySegment<Node>(nodes, i, batchEnd - i)), token, isResponse: true);
        }

        return true;
    }

    private async ValueTask WaitForPendingEndpointBond(IPEndPoint endpoint, NodeSession session, CancellationToken token)
    {
        if (!session.HasEndpointBond(endpoint) && session.HasReceivedPingFrom(endpoint) && session.HasPendingBondingPing(endpoint))
        {
            await session.WaitForEndpointBond(endpoint, _pingTimeout, token);
        }
    }

    private async Task<bool> HandlePing(Node node, NodeSession session, PingMsg ping, CancellationToken token)
    {
        if (Logger.IsTrace) Logger.Trace($"Receive ping from {node:hide}");
        if (ping.Mdc is not { } pingMdc)
        {
            if (Logger.IsTrace) TracePingWithoutHash(node);
            return false;
        }

        PongMsg msg = new(ping.FarAddress!, CalculateExpirationTime(), pingMdc, (await nodeRecordProvider.GetCurrentAsync(token)).EnrSequence);
        IPEndPoint endpoint = node.DiscoveryAddress;
        session.OnPingReceived(endpoint);
        if (!await SendMessage(session, msg, token, isResponse: true, maximumDelay: _pingTimeout)) return false;
        PongMsg? bondingPong = null;
        if (!session.HasEndpointBond(endpoint))
        {
            bondingPong = await TryBond(node, session, token, isInboundBond: true);
            if (bondingPong is null)
            {
                return false;
            }
        }

        ulong? advertisedSequence = HighestSequence(ping.EnrSequence, bondingPong?.EnrSequence);
        await RefreshRemoteRecordIfNewer(node, advertisedSequence, token);
        PublishNode(node, session, ping, advertisedSequence);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceUnbondedEnrRequest(IPEndPoint endpoint, Node node) =>
        Logger.Trace($"Rejecting enr request from unbonded endpoint {endpoint:hide} for peer {node.Id:hide}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceEnrRequestWithoutHash(Node node) =>
        Logger.Trace($"Rejecting enr request without packet hash from {node:hide}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceUnbondedFindNodeRequest(IPEndPoint endpoint, Node node) =>
        Logger.Trace($"Rejecting findNode request from unbonded endpoint {endpoint:hide} for peer {node.Id:hide}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TracePingWithoutHash(Node node) =>
        Logger.Trace($"Rejecting ping without packet hash from {node:hide}");

    private void PublishNode(Node node, NodeSession session, PingMsg? signedPing, ulong? advertisedEnrSequence)
    {
        IPEndPoint discoveryEndpoint = node.DiscoveryAddress;
        if (_recentPeerCandidates.Contains(node.IdHash) || !session.HasEndpointBond(discoveryEndpoint))
        {
            return;
        }

        bool hasUsableSignedPing = signedPing is
        {
            Mdc: not null,
            FarPublicKey: not null,
            FarAddress: not null,
            SourceTcpPort: > 0
        } ping &&
            ping.FarPublicKey.Equals(node.Id) &&
            ping.FarAddress.Equals(discoveryEndpoint);

        Node? enrNode = null;
        Node? verifiedEnrNode = null;
        NodeRecord? record = node.Enr;
        if (record is { Signature: not null } &&
            record.EnrSequence >= node.HighestObservedEnrSequence &&
            (advertisedEnrSequence is null || record.EnrSequence >= advertisedEnrSequence) &&
            _nodeRecordSigner.Verify(record) &&
            Node.TryFromEnr(
                record,
                DiscoveryAddressSupport.GetFamily(discoveryEndpoint.Address),
                out Node? candidate) &&
            candidate.Id.Equals(node.Id))
        {
            bool endpointMatchesEnr = candidate.HasDiscoveryEndpoint && candidate.DiscoveryAddress.Equals(discoveryEndpoint);
            if (endpointMatchesEnr || hasUsableSignedPing)
            {
                candidate.SetVerifiedEnr(record);
                if (endpointMatchesEnr)
                {
                    enrNode = candidate;
                }
                else
                {
                    verifiedEnrNode = candidate;
                }
            }
        }

        bool useSignedPing = hasUsableSignedPing && enrNode is null;
        Node peerCandidate;
        if (useSignedPing)
        {
            peerCandidate = new Node(node.Id, node.Address, node.DiscoveryPort);
            peerCandidate.ObserveEnrSequence(node.HighestObservedEnrSequence);
            if (verifiedEnrNode is not null)
            {
                peerCandidate.MergeEnrStateFrom(verifiedEnrNode);
            }
        }
        else if (enrNode is not null)
        {
            peerCandidate = enrNode;
        }
        else
        {
            return;
        }

        PublishPeerCandidate(peerCandidate);
    }

    private void PublishPeerCandidate(Node node)
    {
        Hash256 nodeId = node.IdHash;
        if (!_recentPeerCandidates.TryReserve(nodeId))
        {
            return;
        }

        if (!_peerCandidates.Writer.TryWrite(node))
        {
            _recentPeerCandidates.Release(nodeId);
        }
    }

    private static ulong? HighestSequence(ulong? first, ulong? second)
        => first is null ? second : second is null ? first : Math.Max(first.Value, second.Value);

    public async Task OnIncomingMsg(DiscoveryMsg msg)
    {
        try
        {
            if (Logger.IsTrace) Logger.Trace($"Received msg: {msg:hide}");
            MsgType msgType = msg.MsgType;
            if (msg.FarPublicKey is null || msg.FarAddress is null)
            {
                if (Logger.IsDebug) Logger.Debug($"Discovery message without a valid remote endpoint or signature, message: {msg:hide}");
                return;
            }

            if (msg.FarPublicKey.Equals(kademliaConfig.CurrentNodeId.Id)) return;

            Node node = CreateNode(msg);

            if (IsResponse(msgType))
            {
                if (!HandleViaMessageHandlers(node, msg, out Node? requestedNode)) return;
                node = requestedNode!;
                MergeKnownEnrState(node);

                NodeSession responseSession = GetSession(node);
                if (msgType != MsgType.Pong) responseSession.RecordStatsForIncomingMsg(msg);
                DiscoveryLiveness.Mark(node);
                node.ValidatedProtocol ??= false;
                nodeHealthTracker.Value.OnIncomingMessageFrom(node);
                return;
            }

            MergeKnownEnrState(node);
            NodeSession session = GetSession(node);

            CancellationToken token = processExitSource.Token;
            switch (msgType)
            {
                case MsgType.Ping:
                    PingMsg ping = (PingMsg)msg;
                    if (!ValidatePingAddress(ping)) return;
                    if (await HandlePing(node, session, ping, token))
                    {
                        session.RecordStatsForIncomingMsg(msg);
                        node.ValidatedProtocol ??= false;
                        nodeHealthTracker.Value.OnIncomingMessageFrom(node);
                    }
                    break;
                case MsgType.FindNode:
                    if (await HandleFindNode(node, session, (FindNodeMsg)msg, token))
                    {
                        session.RecordStatsForIncomingMsg(msg);
                        node.ValidatedProtocol ??= false;
                        nodeHealthTracker.Value.OnIncomingMessageFrom(node);
                    }
                    break;
                case MsgType.EnrRequest:
                    if (await HandleEnrRequest(node, session, (EnrRequestMsg)msg, token))
                    {
                        session.RecordStatsForIncomingMsg(msg);
                        node.ValidatedProtocol ??= false;
                        nodeHealthTracker.Value.OnIncomingMessageFrom(node);
                    }
                    break;
                default:
                    if (Logger.IsError) Logger.Error($"Unsupported msgType: {msgType}");
                    return;
            }
        }
        catch (KademliaRequestNotSentException)
        {
        }
        catch (TaskCanceledException e)
        {
            if (Logger.IsDebug) Logger.Debug($"Error during msg handling. {e}");
        }
        catch (Exception e)
        {
            if (Logger.IsError) Logger.Error("Error during msg handling", e);
        }
    }

    private static bool IsResponse(MsgType msgType) => msgType is MsgType.Neighbors or MsgType.Pong or MsgType.EnrResponse;

    private static Node CreateNode(DiscoveryMsg msg)
    {
        PublicKey farPublicKey = msg.FarPublicKey
            ?? throw new ArgumentException("Discovery message is missing a remote public key.", nameof(msg));
        IPEndPoint farAddress = msg.FarAddress
            ?? throw new ArgumentException("Discovery message is missing a remote endpoint.", nameof(msg));

        return msg is PingMsg { SourceTcpPort: > 0 } ping
            ? new Node(farPublicKey, new IPEndPoint(farAddress.Address, ping.SourceTcpPort), farAddress.Port)
            : Node.FromDiscoveryEndpoint(farPublicKey, farAddress);
    }

    private bool ValidatePingAddress(PingMsg msg)
    {
        if (msg.DestinationAddress is null || msg.FarAddress is null)
        {
            if (Logger.IsError) Logger.Error($"Received a ping message with empty address, message: {msg:hide}");
            return false;
        }

        return true;
    }

    private bool HandleViaMessageHandlers(Node node, DiscoveryMsg msg, out Node? requestedNode)
    {
        requestedNode = null;
        (ValueHash256 IdHash, MsgType MsgType) key = (node.IdHash.ValueHash256, msg.MsgType);
        if (!_incomingMessageHandlers.TryGetValue(key, out IMessageHandler[]? handlers)) return false;
        foreach (IMessageHandler messageHandler in handlers!)
        {
            if (messageHandler.Handle(msg))
            {
                // Note: We dont remove the handler as in case of neighbour, a handler may need multiple message.
                requestedNode = ((EndpointBoundHandler)messageHandler).RequestedNode;
                return true;
            }
        }

        return false;
    }

    public ValueTask DisposeAsync()
    {
        _peerCandidates.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowRequestNotSent() =>
        throw new KademliaRequestNotSentException();

    /// <summary>
    /// The shares of <see cref="IDiscoveryConfig.MaxOutgoingMessagePerSecond"/>: half for responses, an eighth for the
    /// bonding pings that answer inbound pings, so a ping flood cannot use the slots of the node's own requests, and the
    /// rest for those requests. The shares are fractions of a message per second, so they add up to the total for every
    /// value, also below 8. A total below 1 counts as 1.
    /// </summary>
    internal readonly record struct OutgoingRates(double Requests, double Responses, double InboundBonds)
    {
        public static OutgoingRates Split(int maxOutgoingMessagePerSecond)
        {
            double total = Math.Max(1, maxOutgoingMessagePerSecond);
            double responses = total / 2;
            double inboundBonds = total / 8;
            return new OutgoingRates(total - responses - inboundBonds, responses, inboundBonds);
        }
    }
}
