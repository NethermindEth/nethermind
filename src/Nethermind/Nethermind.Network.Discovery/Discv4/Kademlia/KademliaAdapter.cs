// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NonBlocking;

namespace Nethermind.Network.Discovery.Discv4.Kademlia;

public sealed class KademliaAdapter(
    Lazy<IKademlia<PublicKey, Node>> kademlia, // Cyclic dependency
    Lazy<INodeHealthTracker<Node>> nodeHealthTracker,
    IDiscoveryConfig discoveryConfig,
    KademliaConfig<Node> kademliaConfig,
    INodeRecordProvider nodeRecordProvider,
    INodeStatsManager nodeStatsManager,
    ITimestamper timestamper,
    IProcessExitSource processExitSource,
    ILogManager logManager
) : IKademliaAdapter
{
    private const int MaxNodesPerNeighborsMsg = 12;

    private readonly TimeSpan _requestEnrTimeout = TimeSpan.FromMilliseconds(discoveryConfig.EnrTimeout);
    private readonly TimeSpan _findNeighbourTimeout = TimeSpan.FromMilliseconds(discoveryConfig.SendNodeTimeout);
    private readonly TimeSpan _pingTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout);
    private readonly TimeSpan _expirationTime = TimeSpan.FromMilliseconds(discoveryConfig.MessageExpiryTime);
    private readonly TimeSpan _waitAfterPongDelay = TimeSpan.FromMilliseconds(discoveryConfig.BondWaitTime);

    private readonly ILogger _logger = logManager.GetClassLogger<KademliaAdapter>();
    private readonly RateLimiter _outboundRateLimiter = new(discoveryConfig.MaxOutgoingMessagePerSecond);
    public IMsgSender? MsgSender { get; set; }

    private readonly ConcurrentDictionary<(ValueHash256, MsgType), IMessageHandler[]> _incomingMessageHandlers = new();
    private readonly LruCache<ValueHash256, NodeSession> _sessions = new(discoveryConfig.MaxNodeLifecycleManagersCount, "node_sessions");

    public NodeSession GetSession(Node node) => _sessions.SetOrGet(
        node.IdHash.ValueHash256,
        (node, nodeStatsManager, timestamper),
        static (_, state) => new NodeSession(state.nodeStatsManager.GetOrAdd(state.node), state.timestamper));

    private async Task<bool> EnsureOutgoingMessageBondedPeer(Node node, NodeSession nodeSession, CancellationToken token)
    {
        // If we have received ping, then we have ponged which mean we should be bonded from their point of view
        if (nodeSession is { HasReceivedPing: true, NotTooManyFailure: true }) return true;

        if (_logger.IsTrace) _logger.Trace($"Ensure session for node {node}");
        if (!await Ping(node, token)) return false;
        // We send them ping. But expect that eventually they send back another a ping so that we can pong.
        // Give some time for peer to process pong. Such is the logic from geth codebase.
        await Task.Delay(_waitAfterPongDelay, token);

        if (_logger.IsTrace) _logger.Trace($"Node {node} pong sent.");
        return true;
    }

    private async Task<DiscoveryResponse<T>> RunAuthenticatedRequest<T>(Node node, NodeSession session, Func<CancellationToken, Task<DiscoveryResponse<T>>> callRequest, CancellationToken token)
    {
        if (!await EnsureOutgoingMessageBondedPeer(node, session, token))
        {
            session.OnAuthenticatedRequestFailure();
            return DiscoveryResponse<T>.None;
        }

        DiscoveryResponse<T> resp = await callRequest(token);
        if (resp.HasResponse)
        {
            session.ResetAuthenticatedRequestFailure();
        }
        else
        {
            session.OnAuthenticatedRequestFailure();
        }

        return resp;
    }

    private void AddMessageHandler(
        MsgType msgType, ValueHash256 nodeId, IMessageHandler handler) => _incomingMessageHandlers.AddOrUpdate(
            (nodeId, msgType),
            (_) => [handler],
            (_, currentHandler) => [.. currentHandler, handler]
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
        CancellationToken token
    )
    {
        using CancellationTokenSource timeoutCts = new(timeout);
        using CancellationTokenSource sendCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
        using CancellationTokenRegistration cancelRegistration = token.Register(
            static state => ((TaskCompletionSource<DiscoveryResponse<T>>)state!).TrySetCanceled(),
            messageHandler.TaskCompletionSource);
        using CancellationTokenRegistration timeoutRegistration = timeoutCts.Token.Register(
            static state => ((TaskCompletionSource<DiscoveryResponse<T>>)state!).TrySetResult(DiscoveryResponse<T>.None),
            messageHandler.TaskCompletionSource);

        AddMessageHandler(msgType, receiver.IdHash, messageHandler);
        try
        {
            try
            {
                await SendMessage(session, msg, sendCts.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                messageHandler.TaskCompletionSource.TrySetResult(DiscoveryResponse<T>.None);
            }

            DiscoveryResponse<T> response = await messageHandler.TaskCompletionSource.Task;
            if (!response.HasResponse)
            {
                token.ThrowIfCancellationRequested();
                nodeHealthTracker.Value.OnRequestFailed(receiver);
            }

            return response;
        }
        finally
        {
            RemoveMessageHandler(msgType, receiver.IdHash, messageHandler);
        }
    }


    private async Task SendMessage(NodeSession session, DiscoveryMsg msg, CancellationToken token)
    {
        if (MsgSender is { } sender)
        {
            await _outboundRateLimiter.WaitAsync(token);
            session.RecordStatsForOutgoingMsg(msg);
            await sender.SendMsg(msg);
        }
    }

    private long CalculateExpirationTime() => (long)(_expirationTime.TotalSeconds + timestamper.UnixTime.SecondsLong);

    public async Task<bool> Ping(Node receiver, CancellationToken token)
    {
        NodeSession session = GetSession(receiver);

        PingMsg msg = new(receiver.Address, CalculateExpirationTime(), kademliaConfig.CurrentNodeId.Address)
        {
            EnrSequence = (await nodeRecordProvider.GetCurrentAsync(token)).EnrSequence // optional and does not seem to be used anywhere.
        };
        session.OnPingSent();
        DiscoveryResponse<PongMsg> response = await CallAndWaitForResponse(MsgType.Pong, new PongMsgHandler(msg), receiver, session, msg, _pingTimeout, token);
        if (!response.HasResponse) return false;

        session.OnPongReceived(response.Value.FarAddress ?? receiver.Address);
        return true;
    }

    public async Task<Node[]?> FindNeighbours(Node receiver, PublicKey target, CancellationToken token)
    {
        NodeSession session = GetSession(receiver);
        DiscoveryResponse<Node[]> response = await RunAuthenticatedRequest(receiver, session, token =>
        {
            FindNodeMsg msg = new(receiver.Address, CalculateExpirationTime(), target.Bytes);

            return CallAndWaitForResponse(MsgType.Neighbors, new NeighbourMsgHandler(discoveryConfig.BucketSize), receiver, session, msg, _findNeighbourTimeout, token);
        }, token);

        return response.HasResponse ? response.Value : null;
    }

    public async Task<EnrResponseMsg?> SendEnrRequest(Node receiver, CancellationToken token)
    {
        NodeSession session = GetSession(receiver);
        DiscoveryResponse<EnrResponseMsg> response = await RunAuthenticatedRequest(receiver, session, token =>
        {
            EnrRequestMsg msg = new(receiver.Address, CalculateExpirationTime());

            return CallAndWaitForResponse(MsgType.EnrResponse, new EnrResponseHandler(msg), receiver, session, msg, _requestEnrTimeout, token);
        }, token);

        return response.HasResponse ? response.Value : null;
    }

    private async Task<bool> HandleEnrRequest(Node node, NodeSession session, EnrRequestMsg msg, CancellationToken token)
    {
        if (!session.HasEndpointProof(node.Address))
        {
            if (_logger.IsDebug) _logger.Debug($"Rejecting enr request from unbonded peer {node}");
            return false;
        }

        if (msg.Hash is not { } requestHash)
        {
            if (_logger.IsDebug) _logger.Debug($"Rejecting enr request without packet hash from {node}");
            return false;
        }

        await SendMessage(session, new EnrResponseMsg(node.Address, await nodeRecordProvider.GetCurrentAsync(token), new Hash256(requestHash)), token);
        return true;
    }

    private async Task<bool> HandleFindNode(Node node, NodeSession session, FindNodeMsg msg, CancellationToken token)
    {
        if (!session.HasEndpointProof(node.Address))
        {
            if (_logger.IsDebug) _logger.Debug($"Rejecting findNode request from unbonded peer {node}");
            return false;
        }

        PublicKey publicKey = new(msg.SearchedNodeId);
        Node[] nodes = kademlia.Value.GetKNeighbour(publicKey, node, false);
        if (nodes.Length == 0)
        {
            await SendMessage(session, new NeighborsMsg(node.Address, CalculateExpirationTime(), nodes), token);
            return true;
        }

        for (int i = 0; i < nodes.Length; i += MaxNodesPerNeighborsMsg)
        {
            int batchEnd = Math.Min(i + MaxNodesPerNeighborsMsg, nodes.Length);
            await SendMessage(session, new NeighborsMsg(node.Address, CalculateExpirationTime(), new ArraySegment<Node>(nodes, i, batchEnd - i)), token);
        }

        return true;
    }

    private async Task HandlePing(Node node, NodeSession session, PingMsg ping, CancellationToken token)
    {
        if (_logger.IsTrace) _logger.Trace($"Receive ping from {node}");
        if (ping.Mdc is not { } pingMdc)
        {
            if (_logger.IsDebug) _logger.Debug($"Rejecting ping without packet hash from {node}");
            return;
        }

        PongMsg msg = new(ping.FarAddress!, CalculateExpirationTime(), pingMdc);
        session.OnPingReceived();
        await SendMessage(session, msg, token);

        if (!session.HasReceivedPong)
        {
            // If we have never received any pong, then this peer is not bonded and we should not respond to any auth request.
            // Send a ping to bond the peer.
            _ = await Ping(node, token);
        }
    }

    public async Task OnIncomingMsg(DiscoveryMsg msg)
    {
        try
        {
            if (_logger.IsTrace) _logger.Trace($"Received msg: {msg}");
            MsgType msgType = msg.MsgType;
            Node node = new(msg.FarPublicKey, msg.FarAddress);

            if (IsResponse(msgType))
            {
                if (!HandleViaMessageHandlers(node, msg)) return;

                NodeSession responseSession = GetSession(node);
                responseSession.RecordStatsForIncomingMsg(msg);
                nodeHealthTracker.Value.OnIncomingMessageFrom(node);
                return;
            }

            NodeSession session = GetSession(node);
            session.RecordStatsForIncomingMsg(msg);

            CancellationToken token = processExitSource.Token;
            switch (msgType)
            {
                case MsgType.Ping:
                    PingMsg ping = (PingMsg)msg;
                    if (!ValidatePingAddress(ping)) return;
                    await HandlePing(node, session, ping, token);
                    nodeHealthTracker.Value.OnIncomingMessageFrom(node);
                    break;
                case MsgType.FindNode:
                    if (await HandleFindNode(node, session, (FindNodeMsg)msg, token))
                    {
                        nodeHealthTracker.Value.OnIncomingMessageFrom(node);
                    }
                    break;
                case MsgType.EnrRequest:
                    if (await HandleEnrRequest(node, session, (EnrRequestMsg)msg, token))
                    {
                        nodeHealthTracker.Value.OnIncomingMessageFrom(node);
                    }
                    break;
                default:
                    if (_logger.IsError) _logger.Error($"Unsupported msgType: {msgType}");
                    return;
            }
        }
        catch (TaskCanceledException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Error during msg handling. {e}");
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Error during msg handling", e);
        }
    }

    private static bool IsResponse(MsgType msgType) => msgType is MsgType.Neighbors or MsgType.Pong or MsgType.EnrResponse;

    private bool ValidatePingAddress(PingMsg msg)
    {
        if (msg.DestinationAddress is null || msg.FarAddress is null)
        {
            if (_logger.IsError) _logger.Error($"Received a ping message with empty address, message: {msg}");
            return false;
        }

        return true;
    }

    private bool HandleViaMessageHandlers(Node node, DiscoveryMsg msg)
    {
        (ValueHash256 IdHash, MsgType MsgType) key = (node.IdHash.ValueHash256, msg.MsgType);
        if (!_incomingMessageHandlers.TryGetValue(key, out IMessageHandler[]? handlers)) return false;
        foreach (IMessageHandler messageHandler in handlers!)
        {
            if (messageHandler.Handle(msg))
            {
                // Note: We dont remove the handler as in case of neighbour, a handler may need multiple message.
                return true;
            }
        }

        return false;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
