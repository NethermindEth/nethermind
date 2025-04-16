// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.WireProtocol.Session;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NonBlocking;

namespace Nethermind.Network.Discovery;

public class KademliaDiscv4MessageSender(
    INetworkConfig networkConfig,
    KademliaConfig<Node> kademliaConfig,
    ITimestamper timestamper
): IKademliaMessageSender<Node>
{
    public IMsgSender? MsgSender { get; set; }
    public NodeFilter NodesFilter = new((networkConfig?.MaxActivePeers * 4) ?? 200);
    private TimeSpan _requestTimeout = TimeSpan.FromSeconds(5);

    private ConcurrentDictionary<ValueHash256, TaskCompletionSource> _awaitingPingMsg = new();

    // TODO: Allow multiple in flight request per node
    private ConcurrentDictionary<ValueHash256, TaskCompletionSource<Node[]>> _awaitingFindNeighbourMsg = new();
    private ConcurrentDictionary<ValueHash256, TaskCompletionSource<EnrResponseMsg>> _awaitingEnrRequestMsg = new();

    public async Task Ping(Node receiver, CancellationToken token)
    {
        using var cts = token.CreateChildTokenSource(_requestTimeout);
        token = cts.Token;

        PingMsg msg = new PingMsg(receiver.Address, CalculateExpirationTime(), kademliaConfig.CurrentNodeId.Address);
        await SendDiscV4Message(msg);
        ValueHash256 mdc = new ValueHash256(msg.Mdc!); // Mdc is populated after serialization

        TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration unregister = token.RegisterToCompletionSource(completionSource);
        try
        {
            _awaitingPingMsg.TryAdd(mdc, completionSource);
            await completionSource.Task;
        }
        finally
        {
            unregister.Unregister();
            _awaitingPingMsg.TryRemove(mdc, out _);
        }
    }

    public async Task<Node[]> FindNeighbours(Node receiver, ValueHash256 hash, CancellationToken token)
    {
        using var cts = token.CreateChildTokenSource(_requestTimeout);
        token = cts.Token;

        FindNodeMsg msg = new FindNodeMsg(receiver.Address, CalculateExpirationTime(), hash.ToByteArray());
        ValueHash256 requestHash = receiver.IdHash;

        TaskCompletionSource<Node[]> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration unregister = token.RegisterToCompletionSource(completionSource);
        while (!_awaitingFindNeighbourMsg.TryAdd(requestHash, completionSource))
        {
            if (_awaitingFindNeighbourMsg.TryGetValue(requestHash, out TaskCompletionSource<Node[]>? tcs))
            {
                try
                {
                    await tcs.Task;
                }
                finally
                {
                    _awaitingFindNeighbourMsg.TryRemove(requestHash, out _);
                }
            }
        }

        await SendDiscV4Message(msg);
        try
        {
            return await completionSource.Task;
        }
        finally
        {
            unregister.Unregister();
            _awaitingFindNeighbourMsg.TryRemove(requestHash, out _);
        }
    }

    public async Task<EnrResponseMsg> SendEnrRequest(Node receiver, CancellationToken token)
    {
        using var cts = token.CreateChildTokenSource(_requestTimeout);
        token = cts.Token;

        EnrRequestMsg msg = new EnrRequestMsg(receiver.Address, CalculateExpirationTime());
        ValueHash256 requestHash = receiver.IdHash;

        TaskCompletionSource<EnrResponseMsg> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration unregister = token.RegisterToCompletionSource(completionSource);
        while (!_awaitingEnrRequestMsg.TryAdd(requestHash, completionSource))
        {
            if (_awaitingEnrRequestMsg.TryGetValue(requestHash, out TaskCompletionSource<EnrResponseMsg>? tcs))
            {
                try
                {
                    await tcs.Task;
                }
                finally
                {
                    _awaitingEnrRequestMsg.TryRemove(requestHash, out _);
                }
            }
        }

        await SendDiscV4Message(msg);
        try
        {
            return await completionSource.Task;
        }
        finally
        {
            unregister.Unregister();
            _awaitingEnrRequestMsg.TryRemove(requestHash, out _);
        }
    }

    internal void OnPong(Node node, PongMsg msg)
    {
        ValueHash256 mdc = new ValueHash256(msg.PingMdc);
        if (_awaitingPingMsg.TryRemove(mdc, out TaskCompletionSource? completionSource))
        {
            completionSource.TrySetResult();
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
    }

    public async Task SendDiscV4Message(DiscoveryMsg msg)
    {
        if (MsgSender is { } sender)
        {
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
            await sender.SendDiscV4Message(new EnrResponseMsg(node.Address, selfNodeRecord, Keccak.Compute(requestRlp.Bytes)));
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
            await sender.SendDiscV4Message(new NeighborsMsg(node.Address, CalculateExpirationTime(), nodes));
        });
    }

    private void HandlePing(Node node, PingMsg ping)
    {
        Task.Run(async () =>
        {
            await receiver.Ping(node, _cts.Token);
            PongMsg msg = new(ping.FarAddress!, CalculateExpirationTime(), ping.Mdc!);
            await sender.SendDiscV4Message(msg);
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
