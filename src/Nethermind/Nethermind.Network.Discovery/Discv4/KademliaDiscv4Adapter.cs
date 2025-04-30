// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

namespace Nethermind.Network.Discovery;

public class KademliaDiscv4Adapter(
    Lazy<IKademliaMessageReceiver<PublicKey, Node>> kademliaMessageReceiver, // Cyclic dependency
    INetworkConfig networkConfig,
    KademliaConfig<Node> kademliaConfig,
    NodeRecord selfNodeRecord,
    ILogManager logManager,
    ITimestamper timestamper
): IKademliaMessageSender<PublicKey, Node>, IDiscoveryMsgListener, IAsyncDisposable
{
    private ILogger _logger = logManager.GetClassLogger<KademliaDiscv4Adapter>();

    private readonly CancellationTokenSource _cts = new();
    public IMsgSender? MsgSender { get; set; }
    public NodeFilter NodesFilter = new((networkConfig?.MaxActivePeers * 4) ?? 200);
    private TimeSpan _requestTimeout = TimeSpan.FromSeconds(10);
    private TimeSpan _tryAuthenticatedTimeout = TimeSpan.FromSeconds(2);
    private TimeSpan _waitAfterPongTimeout = TimeSpan.FromMilliseconds(500);

    private interface IMessageHandler
    {
        bool Handle(DiscoveryMsg msg);
    }

    private interface ITaskCompleter<T>: IMessageHandler
    {
        TaskCompletionSource<T> TaskCompletionSource { get; }
    }

    private class PongMsgHandler(PingMsg ping) : ITaskCompleter<PongMsg>
    {
        public TaskCompletionSource<PongMsg> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Handle(DiscoveryMsg msg)
        {
            if (msg is PongMsg pong && Bytes.AreEqual(pong.PingMdc, ping.Mdc) && TaskCompletionSource.TrySetResult(pong))
            {
                return true;
            }
            return false;
        }
    }

    private class NeighbourMsgHandler(int k) : ITaskCompleter<Node[]>
    {
        private Node[] _current = Array.Empty<Node>();
        public TaskCompletionSource<Node[]> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static readonly TimeSpan _secondRequestTimeout = TimeSpan.FromSeconds(1);
        private bool _timeoutInitiated = false;

        public bool Handle(DiscoveryMsg msg)
        {
            NeighborsMsg neighborsMsg = (NeighborsMsg)msg;
            if (_current.Length >= k || _current.Length + neighborsMsg.Nodes.Length > k) return false;

            _current = _current.Concat(neighborsMsg.Nodes).ToArray();
            if (_current.Length == k)
            {
                TaskCompletionSource.TrySetResult(_current);
            }
            else
            {
                // Some client (nethermind) only respond with one request.
                Task.Run(async () =>
                {
                    if (Interlocked.CompareExchange(ref _timeoutInitiated, !_timeoutInitiated, false) == false) return;
                    await Task.Delay(_secondRequestTimeout);
                    TaskCompletionSource.TrySetResult(_current);
                });
            }
            return true;
        }
    }

    private class EnrResponseHandler : ITaskCompleter<EnrResponseMsg> {
        public TaskCompletionSource<EnrResponseMsg> TaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Handle(DiscoveryMsg msg)
        {
            if (msg is EnrResponseMsg resp && TaskCompletionSource.TrySetResult(resp))
            {
                return true;
            }
            return false;
        }
    }

    private readonly ConcurrentDictionary<ValueHash256, TaskCompletionSource> _awaitingPongToNode = new();
    private readonly ConcurrentDictionary<(ValueHash256, MsgType), IMessageHandler[]> _incomingMessageHandlers = new();

    private readonly LruCache<ValueHash256, DateTimeOffset> _bondDeadline = new(1024 * 10, "");
    private readonly ConcurrentDictionary<ValueHash256, long> _authenticatedRequestFailure = new();
    private static TimeSpan _bondTimeout = TimeSpan.FromHours(12);
    private const int AuthenticatedRequestFailureLimit = 5;

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

    private void HandleEnrRequest(Node node, EnrRequestMsg msg)
    {
        if (!IsPeerSafe(node)) return;

        Task.Run(async () =>
        {
            Rlp requestRlp = Rlp.Encode(Rlp.Encode(msg.ExpirationTime));
            await SendMessage(node, new EnrResponseMsg(node.Address, selfNodeRecord, Keccak.Compute(requestRlp.Bytes)));
        });
    }

    private void HandleFindNode(Node node, FindNodeMsg msg)
    {
        if (!IsPeerSafe(node)) return;

        Task.Run(async () =>
        {
            PublicKey publicKey = new PublicKey(msg.SearchedNodeId);
            Node[] nodes = await kademliaMessageReceiver.Value.FindNeighbours(node, publicKey, _cts.Token);
            if (nodes.Length > 12)
            {
                // some issue with large neighbour message. Too large, and its larger than the default mtu 1280.
                nodes = nodes.Slice(0, 12).ToArray();
            }
            await SendMessage(node, new NeighborsMsg(node.Address, CalculateExpirationTime(), nodes));
        });
    }

    private void HandlePing(Node node, PingMsg ping)
    {
        if (_logger.IsTrace) _logger.Trace($"Receive ping from {node}");
        Task.Run(async () =>
        {
            await kademliaMessageReceiver.Value.Ping(node, _cts.Token);
            PongMsg msg = new(ping.FarAddress!, CalculateExpirationTime(), ping.Mdc!);
            await SendMessage(node, msg);
        });
    }

    private bool IsShouldBeBonded(Node node)
    {
        return _bondDeadline.TryGet(node.IdHash, out DateTimeOffset bondDeadline)
               && bondDeadline > DateTimeOffset.Now
               && (!_authenticatedRequestFailure.TryGetValue(node.IdHash, out long failedFinedNodes) || failedFinedNodes <= AuthenticatedRequestFailureLimit);
    }

    private async Task<bool> EnsureBonded(Node node, CancellationToken token)
    {
        if (IsShouldBeBonded(node)) return true;

        if (_logger.IsTrace) _logger.Trace($"Ensure session for node {node}");
        using var cts = token.CreateChildTokenSource(_tryAuthenticatedTimeout);
        token = cts.Token;
        TaskCompletionSource pongCts = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration unregister = token.RegisterToCompletionSource(pongCts);
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
            unregister.Unregister();
            _awaitingPongToNode.TryRemove(node.IdHash, out _);
        }
    }

    private async Task<T> RunAuthenticatedRequest<T>(Node node, Func<CancellationToken, Task<T>> callRequest, CancellationToken token)
    {
        bool shouldBeBonded = await EnsureBonded(node, token);
        try
        {
            T resp = await callRequest(token);
            if (!shouldBeBonded)
            {
                // Well.... maybe we already bonded, we just forgot about it....
                _bondDeadline.Set(node.IdHash, DateTimeOffset.Now + _bondTimeout);
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
                _bondDeadline.Set(node.IdHash, DateTimeOffset.Now + _bondTimeout);
                if (_awaitingPongToNode.TryGetValue(node.IdHash, out TaskCompletionSource? completionSource))
                {
                    completionSource.TrySetResult();
                }
            }

            await sender.SendMsg(msg);
        }
    }

    /// <summary>
    /// This is the value set by other clients based on real network tests.
    /// </summary>
    private const int ExpirationTimeInSeconds = 20;
    private long CalculateExpirationTime()
    {
        return ExpirationTimeInSeconds + timestamper.UnixTime.SecondsLong;
    }

    public void OnIncomingMsg(DiscoveryMsg msg)
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
                    HandlePing(node, ping);
                    break;
                case MsgType.FindNode:
                    HandleFindNode(node, (FindNodeMsg)msg);
                    break;
                case MsgType.EnrRequest:
                    HandleEnrRequest(node, (EnrRequestMsg)msg);
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
        var key = (node.IdHash, msg.MsgType);
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

    private bool IsPeerSafe(Node node)
    {
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
