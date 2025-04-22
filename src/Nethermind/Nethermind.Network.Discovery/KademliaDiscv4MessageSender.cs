// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
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
using Prometheus;

namespace Nethermind.Network.Discovery;

public class KademliaDiscv4MessageSender(
    INetworkConfig networkConfig,
    KademliaConfig<Node> kademliaConfig,
    ILogManager logManager,
    ITimestamper timestamper
): IKademliaMessageSender<Node>
{
    private ILogger _logger = logManager.GetClassLogger<KademliaDiscv4MessageSender>();
    public IMsgSender? MsgSender { get; set; }
    public NodeFilter NodesFilter = new((networkConfig?.MaxActivePeers * 4) ?? 200);
    private TimeSpan _unauthenticatedRequestTimeout = TimeSpan.FromSeconds(2.5);
    private TimeSpan _requestTimeout = TimeSpan.FromSeconds(8);
    private TimeSpan _tryAuthenticatedTimeout = TimeSpan.FromSeconds(1);
    private TimeSpan _waitAfterPongTimeout = TimeSpan.FromMilliseconds(100);

    private ConcurrentDictionary<ValueHash256, TaskCompletionSource<PongMsg>> _awaitingPingMsg = new();
    private ConcurrentDictionary<ValueHash256, TaskCompletionSource> _awaitingPongToNode = new();

    // TODO: Allow multiple in flight request per node
    private ConcurrentDictionary<ValueHash256, TaskCompletionSource<Node[]>> _awaitingFindNeighbourMsg = new();
    private ConcurrentDictionary<ValueHash256, TaskCompletionSource<EnrResponseMsg>> _awaitingEnrRequestMsg = new();

    private LruCache<ValueHash256, DateTimeOffset> _lastPong = new(1024, "");

    private Counter EnsureSessionResult =
        Prometheus.Metrics.CreateCounter("kademlia_ensure_session_result", "result", "result");

    private async Task EnsureSession(Node node, CancellationToken token)
    {
        if (_lastPong.TryGet(node.IdHash, out DateTimeOffset lastPong) && lastPong > DateTimeOffset.Now - TimeSpan.FromHours(12))
        {
            if (_logger.IsTrace) _logger.Trace($"Node already had pong within deadline {node}. Pong duration: {DateTimeOffset.Now - lastPong}");
            EnsureSessionResult.WithLabels("pong_not_expired");
            return;
        }

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
            EnsureSessionResult.WithLabels("pong_success");
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsTrace) _logger.Trace($"Node {node} timeout trying to trigger pong.");
            _logger.Warn($"Node {node} timeout trying to trigger pong.");
            EnsureSessionResult.WithLabels("pong_timeout");
        }
        catch (Exception)
        {
            EnsureSessionResult.WithLabels("error");
            throw;
        }
        finally
        {
            unregister.Unregister();
            _awaitingPongToNode.TryRemove(node.IdHash, out _);
        }
    }

    private async Task<T> RunAuthenticatedRequest<T>(Node node, Func<CancellationToken, Task<T>> callRequest, CancellationToken token)
    {
        using var cts = token.CreateChildTokenSource(_requestTimeout);
        token = cts.Token;

        {
            using var firstTryCts = token.CreateChildTokenSource(_unauthenticatedRequestTimeout);
            try
            {
                return await callRequest(firstTryCts.Token);
            }
            catch (OperationCanceledException)
            {
                // If we don't get a response in time, it could just be that we are not authenticated there
            }
        }

        await EnsureSession(node, token);

        // Then we just try a final time.
        try
        {
            return await callRequest(token);
        }
        catch (OperationCanceledException)
        {
            _lastPong.Delete(node.IdHash);
            throw;
        }
    }

    public async Task Ping(Node receiver, CancellationToken token)
    {
        using var cts = token.CreateChildTokenSource(_requestTimeout);
        token = cts.Token;

        PingMsg msg = new PingMsg(receiver.Address, CalculateExpirationTime(), kademliaConfig.CurrentNodeId.Address);

        PongMsg pongMsg = await CallAndWaitForResponse(_awaitingPingMsg, receiver, msg, token);
        if (!Bytes.AreEqual(msg.Mdc, pongMsg.PingMdc))
        {
            _logger.Error($"Invalid pong mdc. Send {msg.Mdc?.ToHexString()}, Received {pongMsg.PingMdc?.ToHexString()}");
            throw new OperationCanceledException(); // Expose as timeout
        }
    }

    private async Task<T> CallAndWaitForResponse<T>(
        ConcurrentDictionary<ValueHash256, TaskCompletionSource<T>> requestDictionary,
        Node receiver,
        DiscoveryMsg msg,
        CancellationToken token
    ) {
        TaskCompletionSource<T> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration unregister = token.RegisterToCompletionSource(completionSource);
        ValueHash256 requestHash = receiver.IdHash;
        while (!requestDictionary.TryAdd(requestHash, completionSource))
        {
            if (requestDictionary.TryGetValue(requestHash, out TaskCompletionSource<T>? tcs))
            {
                try
                {
                    await tcs.Task;
                }
                finally
                {
                    requestDictionary.TryRemove(requestHash, out _);
                }
            }
        }

        await SendDiscV4Message(receiver, msg);
        try
        {
            return await completionSource.Task;
        }
        finally
        {
            unregister.Unregister();
            requestDictionary.TryRemove(requestHash, out _);
        }
    }

    public async Task<Node[]> FindNeighbours(Node receiver, ValueHash256 hash, CancellationToken token)
    {
        using var cts = token.CreateChildTokenSource(_requestTimeout);
        token = cts.Token;

        return await RunAuthenticatedRequest(receiver, async token =>
        {
            FindNodeMsg msg = new FindNodeMsg(receiver.Address, CalculateExpirationTime(), hash.ToByteArray());

            return await CallAndWaitForResponse(_awaitingFindNeighbourMsg, receiver, msg, token);
        }, token);
    }

    public async Task<EnrResponseMsg> SendEnrRequest(Node receiver, CancellationToken token)
    {
        using var cts = token.CreateChildTokenSource(_requestTimeout);
        token = cts.Token;

        return await RunAuthenticatedRequest(receiver, async token =>
        {
            EnrRequestMsg msg = new EnrRequestMsg(receiver.Address, CalculateExpirationTime());

            return await CallAndWaitForResponse(_awaitingEnrRequestMsg, receiver, msg, token);
        }, token);
    }

    public async Task<EnrResponseMsg> SendUnauthEnrRequest(Node receiver, CancellationToken token)
    {
        using var cts = token.CreateChildTokenSource(_requestTimeout);
        token = cts.Token;

        EnrRequestMsg msg = new EnrRequestMsg(receiver.Address, CalculateExpirationTime());
        return await CallAndWaitForResponse(_awaitingEnrRequestMsg, receiver, msg, token);
    }

    internal void OnPong(Node node, PongMsg msg)
    {
        if (_awaitingPingMsg.TryRemove(node.IdHash, out TaskCompletionSource<PongMsg>? completionSource))
        {
            completionSource.TrySetResult(msg);
        }
        else
        {
            _logger.Error($"No ping for pong {node} {msg.PingMdc.ToHexString()}");
        }
    }

    public void HandleEnrResponse(Node node, EnrResponseMsg msg)
    {
        ValueHash256 requestId = node.IdHash;
        if (_awaitingEnrRequestMsg.TryRemove(requestId, out TaskCompletionSource<EnrResponseMsg>? completionSource))
        {
            completionSource.TrySetResult(msg);
        }
    }

    public void OnNeighbour(Node node, NeighborsMsg msg)
    {
        ValueHash256 requestId = node.IdHash;
        if (_awaitingFindNeighbourMsg.TryRemove(requestId, out TaskCompletionSource<Node[]>? completionSource))
        {
            completionSource.TrySetResult(msg.Nodes);
        }
        else
        {
            _logger.Error($"No FindNeighbour for Neighbour {node} {msg}");
        }
    }

    public async Task SendDiscV4Message(Node node, DiscoveryMsg msg)
    {
        if (MsgSender is { } sender)
        {
            if (msg is PongMsg pong)
            {
                _lastPong.Set(node.IdHash, DateTimeOffset.Now);
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
}

#pragma warning disable CS9113 // Parameter is unread.
public class KademliaDiscv4MessageReceiver(
     IKademliaMessageReceiver<Node> receiver,
     KademliaDiscv4MessageSender sender,
     NodeRecord selfNodeRecord,
     ITimestamper timestamper,
     ILogManager logManager
) : IDiscoveryMsgListener, IAsyncDisposable
#pragma warning restore CS9113 // Parameter is unread.
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly CancellationTokenSource _cts = new();

    public void OnIncomingMsg(DiscoveryMsg msg)
    {
        try
        {
            if (_logger.IsTrace) _logger.Trace($"Received msg: {msg}");
            MsgType msgType = msg.MsgType;
            Node node = new(msg.FarPublicKey, msg.FarAddress);

            switch (msgType)
            {
                case MsgType.Neighbors:
                    sender.OnNeighbour(node, (NeighborsMsg)msg);
                    break;
                case MsgType.Pong:
                    sender.OnPong(node, (PongMsg)msg);
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
                    sender.HandleEnrResponse(node, (EnrResponseMsg)msg);
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

    private void HandleEnrRequest(Node node, EnrRequestMsg msg)
    {
        if (!IsPeerSafe(node)) return;

        Task.Run(async () =>
        {
            Rlp requestRlp = Rlp.Encode(Rlp.Encode(msg.ExpirationTime));
            await sender.SendDiscV4Message(node, new EnrResponseMsg(node.Address, selfNodeRecord, Keccak.Compute(requestRlp.Bytes)));
        });
    }

    private void HandleFindNode(Node node, FindNodeMsg msg)
    {
        if (!IsPeerSafe(node)) return;

        Task.Run(async () =>
        {
            ValueHash256 searchId = new ValueHash256(msg.SearchedNodeId);
            Node[] nodes = await receiver.FindNeighbours(node, searchId, _cts.Token);
            if (nodes.Length > 12)
            {
                // some issue with large neighbour message. Too large, and its larger than the default mtu 1280.
                nodes = nodes.Slice(0, 12).ToArray();
            }
            await sender.SendDiscV4Message(node, new NeighborsMsg(node.Address, CalculateExpirationTime(), nodes));
        });
    }

    private void HandlePing(Node node, PingMsg ping)
    {
        if (_logger.IsTrace) _logger.Trace($"Receive ping from {node}");
        Task.Run(async () =>
        {
            await receiver.Ping(node, _cts.Token);
            PongMsg msg = new(ping.FarAddress!, CalculateExpirationTime(), ping.Mdc!);
            await sender.SendDiscV4Message(node, msg);
        });
    }

    private bool IsPeerSafe(Node node)
    {
        return true;
    }

    /// <summary>
    /// This is the value set by other clients based on real network tests.
    /// </summary>
    private const int ExpirationTimeInSeconds = 20;
    private long CalculateExpirationTime()
    {
        return ExpirationTimeInSeconds + timestamper.UnixTime.SecondsLong;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
