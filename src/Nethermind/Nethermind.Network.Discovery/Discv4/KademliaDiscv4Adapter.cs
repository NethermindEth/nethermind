// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NonBlocking;

namespace Nethermind.Network.Discovery.Discv4;

public class KademliaDiscv4Adapter(
    Lazy<IKademliaMessageReceiver<PublicKey, Node>> kademliaMessageReceiver, // Cyclic dependency
    INetworkConfig networkConfig,
    KademliaConfig<Node> kademliaConfig,
    NodeRecord selfNodeRecord,
    ITimestamper timestamper,
    IProcessExitSource processExitSource,
    ILogManager logManager
): IKademliaMessageSender<PublicKey, Node>, IDiscoveryMsgListener, IAsyncDisposable
{
    private static readonly TimeSpan BondTimeout = TimeSpan.FromHours(12);
    private readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _tryAuthenticatedTimeout = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _waitAfterPongTimeout = TimeSpan.FromMilliseconds(500);
    private const int AuthenticatedRequestFailureLimit = 5;
    /// <summary>
    /// This is the value set by other clients based on real network tests.
    /// </summary>
    private const int ExpirationTimeInSeconds = 20;

    private readonly ILogger _logger = logManager.GetClassLogger<KademliaDiscv4Adapter>();
    public IMsgSender? MsgSender { get; set; }
    public NodeFilter NodesFilter = new((networkConfig?.MaxActivePeers * 4) ?? 200);

    private readonly ConcurrentDictionary<(ValueHash256, MsgType), IMessageHandler[]> _incomingMessageHandlers = new();

    private readonly ConcurrentDictionary<ValueHash256, TaskCompletionSource<object>> _awaitingPongToNode = new(); // This is for waiting to send pong in attempt to authenticate.
    private readonly LruCache<ValueHash256, DateTimeOffset> _outgoingBondDeadline = new(1024 * 10, "outgoing_bond_deadline");

    private readonly LruCache<ValueHash256, DateTimeOffset> _incomingBondDeadline = new(1024 * 10, "incoming_bond_deadline");
    private readonly ConcurrentDictionary<ValueHash256, long> _authenticatedRequestFailure = new(); // TODO: To lru cache
    private readonly CancellationToken _processCancellationToken = processExitSource.Token;

    #region Authentication and utils

    private async Task<bool> EnsureOutgoingMessageBondedPeer(Node node, CancellationToken token)
    {
        if (_outgoingBondDeadline.TryGet(node.IdHash, out DateTimeOffset bondDeadline)
            && bondDeadline > DateTimeOffset.Now
            && !TooManyFailure()) return true;

        if (_logger.IsTrace) _logger.Trace($"Ensure session for node {node}");
        using var cts = token.CreateChildTokenSource(_tryAuthenticatedTimeout);
        token = cts.Token;
        TaskCompletionSource<object> pongCts = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CancellationTokenRegistration unregister = token.RegisterToCompletionSource(pongCts);
        try
        {
            _awaitingPongToNode.TryAdd(node.IdHash, pongCts);
            await Ping(node, token);
            await pongCts.Task;
            await Task.Delay(_waitAfterPongTimeout, token); // Give some time for peer to process pong.

            if (_logger.IsTrace) _logger.Trace($"Node {node} pong sent.");

            return true;
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsTrace) _logger.Trace($"Node {node} timeout trying to trigger pong.");

            return false;
        }
        finally
        {
            _awaitingPongToNode.TryRemove(node.IdHash, out _);
        }

        bool TooManyFailure()
        {
            return _authenticatedRequestFailure.TryGetValue(node.IdHash, out long failedFinedNodes) && failedFinedNodes > AuthenticatedRequestFailureLimit;
        }
    }

    private async Task EnsureIncomingMessageBondedPeer(Node node, CancellationToken token)
    {
        if (_incomingBondDeadline.TryGet(node.IdHash, out DateTimeOffset safeUntil) && safeUntil > DateTimeOffset.Now)
        {
            return;
        }

        // If we're here, the node is not safe, so we'll send a ping to verify
        await Ping(node, token);
        _incomingBondDeadline.Set(node.IdHash, DateTimeOffset.Now + BondTimeout);
    }

    private async Task<T> RunAuthenticatedRequest<T>(Node node, Func<CancellationToken, Task<T>> callRequest, CancellationToken token)
    {
        bool shouldBeBonded = await EnsureOutgoingMessageBondedPeer(node, token);
        try
        {
            T resp = await callRequest(token);
            if (!shouldBeBonded)
            {
                // Well.... maybe we already bonded, we just forgot about it....
                _outgoingBondDeadline.Set(node.IdHash, DateTimeOffset.Now + BondTimeout);
            }
            _authenticatedRequestFailure[node.IdHash] = 0;
            return resp;
        }
        catch (OperationCanceledException)
        {
            _authenticatedRequestFailure.Increment(node.IdHash);

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
        DiscoveryMsg msg,
        CancellationToken token
    ) {
        await using CancellationTokenRegistration unregister = token.RegisterToCompletionSource(messageHandler.TaskCompletionSource);
        AddMessageHandler(msgType, receiver.IdHash, messageHandler);

        await SendMessage(receiver, msg);
        try
        {
            return await messageHandler.TaskCompletionSource.Task;
        }
        finally
        {
            RemoveMessageHandler(msgType, receiver.IdHash, messageHandler);
        }
    }


    private async Task SendMessage(Node node, DiscoveryMsg msg)
    {
        if (MsgSender is { } sender)
        {
            if (msg is PongMsg pong)
            {
                _outgoingBondDeadline.Set(node.IdHash, DateTimeOffset.Now + BondTimeout);
                if (_awaitingPongToNode.TryGetValue(node.IdHash, out TaskCompletionSource<object>? completionSource))
                {
                    completionSource.TrySetResult(new object());
                }
            }

            await sender.SendMsg(msg);
        }
    }

    private long CalculateExpirationTime()
    {
        return ExpirationTimeInSeconds + timestamper.UnixTime.SecondsLong;
    }



    #endregion

    public async Task Ping(Node receiver, CancellationToken token)
    {
        using var cts = token.CreateChildTokenSource(_requestTimeout);
        token = cts.Token;

        PingMsg msg = new PingMsg(receiver.Address, CalculateExpirationTime(), kademliaConfig.CurrentNodeId.Address);

        _ = await CallAndWaitForResponse(MsgType.Pong, new PongMsgHandler(msg), receiver, msg, token);
    }

    public async Task<Node[]> FindNeighbours(Node receiver, PublicKey target, CancellationToken token)
    {
        using var cts = token.CreateChildTokenSource(_requestTimeout);
        token = cts.Token;

        return await RunAuthenticatedRequest(receiver, async token =>
        {
            FindNodeMsg msg = new FindNodeMsg(receiver.Address, CalculateExpirationTime(), target.Bytes);

            // TODO: 16 is configurable
            return await CallAndWaitForResponse(MsgType.Neighbors, new NeighbourMsgHandler(16), receiver, msg, token);
        }, token);
    }

    public async Task<EnrResponseMsg> SendEnrRequest(Node receiver, CancellationToken token)
    {
        using var cts = token.CreateChildTokenSource(_requestTimeout);
        token = cts.Token;

        return await RunAuthenticatedRequest(receiver, async token =>
        {
            EnrRequestMsg msg = new EnrRequestMsg(receiver.Address, CalculateExpirationTime());

            return await CallAndWaitForResponse(MsgType.EnrResponse, new EnrResponseHandler(), receiver, msg, token);
        }, token);
    }

    private async Task HandleEnrRequest(Node node, EnrRequestMsg msg)
    {
        await EnsureIncomingMessageBondedPeer(node, _processCancellationToken);

        Rlp requestRlp = Rlp.Encode(Rlp.Encode(msg.ExpirationTime));
        await SendMessage(node, new EnrResponseMsg(node.Address, selfNodeRecord, Keccak.Compute(requestRlp.Bytes)));
    }

    private async Task HandleFindNode(Node node, FindNodeMsg msg)
    {
        await EnsureIncomingMessageBondedPeer(node, _processCancellationToken);

        PublicKey publicKey = new PublicKey(msg.SearchedNodeId);
        Node[] nodes = await kademliaMessageReceiver.Value.FindNeighbours(node, publicKey, _processCancellationToken);
        if (nodes.Length <= 12)
        {
            await SendMessage(node, new NeighborsMsg(node.Address, CalculateExpirationTime(), nodes));
        }
        else
        {
            // Split into two because the size of message when nodes is > 12 is larger than mtu size.
            await SendMessage(node, new NeighborsMsg(node.Address, CalculateExpirationTime(), nodes[..12]));
            await SendMessage(node, new NeighborsMsg(node.Address, CalculateExpirationTime(), nodes[12..]));
        }
    }

    private async Task HandlePing(Node node, PingMsg ping)
    {
        if (_logger.IsTrace) _logger.Trace($"Receive ping from {node}");
        await kademliaMessageReceiver.Value.Ping(node, _processCancellationToken);
        // Generate MDC hash from the ping message
        Rlp requestRlp = Rlp.Encode(Rlp.Encode(ping.ExpirationTime));
        byte[] mdc = Keccak.Compute(requestRlp.Bytes).Bytes.ToArray();
        PongMsg msg = new(ping.FarAddress!, CalculateExpirationTime(), mdc);
        await SendMessage(node, msg);
    }

    public async Task OnIncomingMsg(DiscoveryMsg msg)
    {
        try
        {
            if (_logger.IsTrace) _logger.Trace($"Received msg: {msg}");
            MsgType msgType = msg.MsgType;
            Node node = new(msg.FarPublicKey, msg.FarAddress);

            if (HandleViaMessageHandlers(node, msg))
            {
                return;
            }

            switch (msgType)
            {
                case MsgType.Neighbors:
                    break;
                case MsgType.Pong:
                    break;
                case MsgType.Ping:
                    PingMsg ping = (PingMsg)msg;
                    await HandlePing(node, ping);
                    break;
                case MsgType.FindNode:
                    await HandleFindNode(node, (FindNodeMsg)msg);
                    break;
                case MsgType.EnrRequest:
                    await HandleEnrRequest(node, (EnrRequestMsg)msg);
                    break;
                case MsgType.EnrResponse:
                    break;
                default:
                    _logger.Error($"Unsupported msgType: {msgType}");
                    return;
            }
        }
        catch (Exception e)
        {
            _logger.Error("Error during msg handling", e);
        }
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
