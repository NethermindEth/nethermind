// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NonBlocking;

namespace Nethermind.Network.Discovery.Discv4;

public class KademliaDiscv4Adapter(
    Lazy<IKademlia<PublicKey, Node>> kademlia, // Cyclic dependency
    Lazy<INodeHealthTracker<Node>> nodeHealthTracker,
    IDiscoveryConfig discoveryConfig,
    KademliaConfig<Node> kademliaConfig,
    INodeRecordProvider nodeRecordProvider,
    INodeStatsManager nodeStatsManager,
    ITimestamper timestamper,
    IProcessExitSource processExitSource,
    ILogManager logManager
) : IKademliaDiscv4Adapter
{
    private readonly TimeSpan _requestEnrTimeout = TimeSpan.FromMilliseconds(discoveryConfig.EnrTimeout);
    private readonly TimeSpan _findNeighbourTimeout = TimeSpan.FromMilliseconds(discoveryConfig.SendNodeTimeout);
    private readonly TimeSpan _pingTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout);
    private readonly TimeSpan _expirationTime = TimeSpan.FromMilliseconds(discoveryConfig.MessageExpiryTime);
    private readonly TimeSpan _waitAfterPongDelay = TimeSpan.FromMilliseconds(discoveryConfig.BondWaitTime);

    private readonly ILogger _logger = logManager.GetClassLogger<KademliaDiscv4Adapter>();
    private readonly RateLimiter _outboundRateLimiter = new(discoveryConfig.MaxOutgoingMessagePerSecond);
    public IMsgSender? MsgSender { get; set; }

    private readonly ConcurrentDictionary<(ValueHash256, MsgType), IMessageHandler[]> _incomingMessageHandlers = new();
    private readonly LruCache<ValueHash256, NodeSession> _sessions = new(discoveryConfig.MaxNodeLifecycleManagersCount, "node_sessions");

    #region Authentication and utils

    public NodeSession GetSession(Node node)
    {
        if (_sessions.TryGet(node.IdHash, out var session)) return session;
        session = new NodeSession(nodeStatsManager.GetOrAdd(node), timestamper);
        _sessions.Set(node.IdHash, session);
        return session;
    }

    private async Task EnsureOutgoingMessageBondedPeer(Node node, NodeSession nodeSession, CancellationToken token)
    {
        // If we have received ping, then we have ponged which mean we should be bonded from their point of view
        if (nodeSession is { HasReceivedPing: true, NotTooManyFailure: true }) return;

        if (_logger.IsTrace) _logger.Trace($"Ensure session for node {node}");
        await Ping(node, token);
        // We send them ping. But expect that eventually they send back another a ping so that we can pong.
        // Give some time for peer to process pong. Such is the logic from geth codebase.
        await Task.Delay(_waitAfterPongDelay, token);

        if (_logger.IsTrace) _logger.Trace($"Node {node} pong sent.");
    }

    private async Task<T> RunAuthenticatedRequest<T>(Node node, NodeSession session, Func<CancellationToken, Task<T>> callRequest, CancellationToken token)
    {
        await EnsureOutgoingMessageBondedPeer(node, session, token);
        try
        {
            T resp = await callRequest(token);
            session.ResetAuthenticatedRequestFailure();
            return resp;
        }
        catch (OperationCanceledException)
        {
            session.OnAuthenticatedRequestFailure();

            throw;
        }
    }

    private void AddMessageHandler(
        MsgType msgType, ValueHash256 nodeId, IMessageHandler handler)
    {
        _incomingMessageHandlers.AddOrUpdate(
            (nodeId, msgType),
            (_) => [handler],
            (_, currentHandler) => currentHandler.Concat([handler]).ToArray()
        );
    }

    private void RemoveMessageHandler(
        MsgType msgType, ValueHash256 nodeId, IMessageHandler handler)
    {
        var key = (nodeId, msgType);
        if (_incomingMessageHandlers.TryRemove(new KeyValuePair<(ValueHash256, MsgType), IMessageHandler[]>(key, [handler]))) return;

        while (true)
        {
            if (!_incomingMessageHandlers.TryGetValue(key, out IMessageHandler[]? current)) return;
            var newValue = current.Where((it) => it != handler).ToArray();
            if (_incomingMessageHandlers.TryUpdate(key, newValue, current)) return;
        }
    }

    private async Task<T> CallAndWaitForResponse<T>(
        MsgType msgType,
        ITaskCompleter<T> messageHandler,
        Node receiver,
        NodeSession session,
        DiscoveryMsg msg,
        CancellationToken token
    )
    {
        await using CancellationTokenRegistration unregister = token.RegisterToCompletionSource(messageHandler.TaskCompletionSource);
        AddMessageHandler(msgType, receiver.IdHash, messageHandler);

        await SendMessage(session, msg, token);
        try
        {
            return await messageHandler.TaskCompletionSource.Task;
        }
        catch (OperationCanceledException)
        {
            nodeHealthTracker.Value.OnRequestFailed(receiver);
            throw;
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

    private long CalculateExpirationTime()
    {
        return (long)(_expirationTime.TotalSeconds + timestamper.UnixTime.SecondsLong);
    }

    #endregion

    public async Task Ping(Node receiver, CancellationToken token)
    {
        using var cts = token.CreateChildTokenSource(_pingTimeout);
        token = cts.Token;
        NodeSession session = GetSession(receiver);

        PingMsg msg = new PingMsg(receiver.Address, CalculateExpirationTime(), kademliaConfig.CurrentNodeId.Address);
        msg.EnrSequence = nodeRecordProvider.Current.EnrSequence; // optional and does not seems to be used anywhere.
        session.OnPingSent();
        _ = await CallAndWaitForResponse(MsgType.Pong, new PongMsgHandler(msg), receiver, session, msg, token);
        session.OnPongReceived();
    }

    public async Task<Node[]> FindNeighbours(Node receiver, PublicKey target, CancellationToken token)
    {
        NodeSession session = GetSession(receiver);
        return await RunAuthenticatedRequest(receiver, session, async token =>
        {
            using var cts = token.CreateChildTokenSource(_findNeighbourTimeout);
            token = cts.Token;

            FindNodeMsg msg = new FindNodeMsg(receiver.Address, CalculateExpirationTime(), target.Bytes);

            return await CallAndWaitForResponse(MsgType.Neighbors, new NeighbourMsgHandler(discoveryConfig.BucketSize), receiver, session, msg, token);
        }, token);
    }

    public async Task<EnrResponseMsg> SendEnrRequest(Node receiver, CancellationToken token)
    {
        NodeSession session = GetSession(receiver);
        return await RunAuthenticatedRequest(receiver, session, async token =>
        {
            using var cts = token.CreateChildTokenSource(_requestEnrTimeout);
            token = cts.Token;

            EnrRequestMsg msg = new EnrRequestMsg(receiver.Address, CalculateExpirationTime());

            return await CallAndWaitForResponse(MsgType.EnrResponse, new EnrResponseHandler(), receiver, session, msg, token);
        }, token);
    }

    private async Task HandleEnrRequest(Node node, NodeSession session, EnrRequestMsg msg, CancellationToken token)
    {
        if (!session.HasReceivedPong)
        {
            if (_logger.IsDebug) _logger.Debug($"Rejecting enr request from unbonded peer {node}");
            return;
        }

        Rlp requestRlp = Rlp.Encode(Rlp.Encode(msg.ExpirationTime));
        await SendMessage(session, new EnrResponseMsg(node.Address, nodeRecordProvider.Current, Keccak.Compute(requestRlp.Bytes)), token);
    }

    private async Task HandleFindNode(Node node, NodeSession session, FindNodeMsg msg, CancellationToken token)
    {
        if (!session.HasReceivedPong)
        {
            if (_logger.IsDebug) _logger.Debug($"Rejecting findNode request from unbonded peer {node}");
            return;
        }

        PublicKey publicKey = new PublicKey(msg.SearchedNodeId);
        Node[] nodes = kademlia.Value.GetKNeighbour(publicKey, node, false);
        if (nodes.Length <= 12)
        {
            await SendMessage(session, new NeighborsMsg(node.Address, CalculateExpirationTime(), nodes), token);
        }
        else
        {
            // Split into two because the size of message when nodes is > 12 is larger than mtu size.
            await SendMessage(session, new NeighborsMsg(node.Address, CalculateExpirationTime(), nodes[..12]), token);
            await SendMessage(session, new NeighborsMsg(node.Address, CalculateExpirationTime(), nodes[12..16]), token);
        }
    }

    private async Task HandlePing(Node node, NodeSession session, PingMsg ping, CancellationToken token)
    {
        if (_logger.IsTrace) _logger.Trace($"Receive ping from {node}");
        PongMsg msg = new(ping.FarAddress!, CalculateExpirationTime(), ping.Mdc!);
        session.OnPingReceived();
        await SendMessage(session, msg, token);

        if (!session.HasReceivedPong)
        {
            // If we have never received any pong, then this peer is not bonded and we should not respond to any auth request.
            // Send a ping to bond the peer.
            await Ping(node, token);
        }
    }

    public async Task OnIncomingMsg(DiscoveryMsg msg)
    {
        try
        {
            if (_logger.IsTrace) _logger.Trace($"Received msg: {msg}");
            MsgType msgType = msg.MsgType;
            Node node = new(msg.FarPublicKey, msg.FarAddress);
            NodeSession session = GetSession(node);
            session.RecordStatsForIncomingMsg(msg);

            if (HandleViaMessageHandlers(node, msg))
            {
                nodeHealthTracker.Value.OnIncomingMessageFrom(node);
                return;
            }

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
                    await HandleFindNode(node, session, (FindNodeMsg)msg, token);
                    nodeHealthTracker.Value.OnIncomingMessageFrom(node);
                    break;
                case MsgType.EnrRequest:
                    await HandleEnrRequest(node, session, (EnrRequestMsg)msg, token);
                    nodeHealthTracker.Value.OnIncomingMessageFrom(node);
                    break;

                // Unsolicited response.
                case MsgType.Neighbors:
                case MsgType.Pong:
                case MsgType.EnrResponse:
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
        (Hash256 IdHash, MsgType MsgType) key = (node.IdHash, msg.MsgType);
        if (!_incomingMessageHandlers.TryGetValue(key, out IMessageHandler[]? handlers)) return false;
        foreach (var messageHandler in handlers!)
        {
            if (messageHandler.Handle(msg))
            {
                // Note: We dont remove the handler as in case of neighbour, a handler may need multiple message.
                return true;
            }
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
