// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Lantern.Discv5.Enr;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// A "portal content network" is basically a kademlia network on top of discv5 subprotocol via TalkReq with the
/// addition of using UTP (as another discv5 subprotocol) when transferring large binary.
/// This class basically, wire all those things up into an IPortalContentNetwork.
/// The specific interpretation of the key and content is handle at higher level, see `PortalHistoryNetwork` for history network.
/// </summary>
/// <param name="enrProvider"></param>
/// <param name="talkReqTransport"></param>
/// <param name="utpManager"></param>
/// <param name="logManager"></param>
public class PortalContentNetwork : IPortalContentNetwork
{
    private readonly IUtpManager _utpManager;
    private readonly IKademlia<IEnr, byte[], LookupContentResult> _kademlia;
    private readonly IMessageSender<IEnr, byte[], LookupContentResult> _messageSender;
    private readonly IContentDistributor _contentDistributor;
    private readonly ILogger _logger1;

    public PortalContentNetwork(
        ContentNetworkConfig config,
        IUtpManager utpManager,
        IKademlia<IEnr, byte[], LookupContentResult> kademlia,
        ITalkReqProtocolHandler talkReqHandler,
        ITalkReqTransport talkReqTransport,
        IMessageSender<IEnr, byte[], LookupContentResult> messageSender,
        IContentDistributor contentDistributor,
        ILogManager logManager)
    {
        talkReqTransport.RegisterProtocol(config.ProtocolId, talkReqHandler);
        _utpManager = utpManager;
        _kademlia = kademlia;
        _messageSender = messageSender;
        _contentDistributor = contentDistributor;
        _logger1 = logManager.GetClassLogger<PortalContentNetwork>();
        _kademlia.OnNodeAdded += KademliaOnOnNodeAdded;

        foreach (IEnr bootNode in config.BootNodes)
        {
            AddOrRefresh(bootNode);
        }
    }

    private void KademliaOnOnNodeAdded(object? sender, IEnr newNode)
    {
        // So with history network and its radius custom payload, something confusing happen.
        // We never actually send ping except during refresh, and that only happen if a bucket is full.
        // So when do we actually send ping? Its unclear where. But just to make sure I send it when a
        // new node is found.
        if (_logger1.IsDebug) _logger1.Debug($"Ping {newNode.NodeId.ToHexString()} on new node");
        _messageSender.Ping(newNode, CancellationToken.None);
    }

    public async Task<byte[]?> LookupContent(byte[] key, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        token = cts.Token;

        Stopwatch sw = Stopwatch.StartNew();
        var result = await _kademlia.LookupValue(key, token);
        _logger1.Info($"Lookup {key.ToHexString()} took {sw.Elapsed}");

        sw.Restart();

        if (result == null) return null;

        if (result.Payload != null) return result.Payload;

        Debug.Assert(result.ConnectionId != null);

        MemoryStream stream = new MemoryStream();
        await _utpManager.ReadContentFromUtp(result.NodeId, true, result.ConnectionId.Value, stream, token);
        var asBytes = stream.ToArray();
        _logger1.Info($"UTP download for {key.ToHexString()} took {sw.Elapsed}");
        return asBytes;
    }

    public async Task<byte[]?> LookupContentFrom(IEnr node, byte[] contentKey, CancellationToken token)
    {
        var content = await _messageSender.FindValue(node, contentKey, token);
        if (!content.hasValue)
        {
            return null;
        }

        var value = content.value!;
        if (value.Payload != null) return value.Payload;

        MemoryStream stream = new MemoryStream();
        await _utpManager.ReadContentFromUtp(node, true, value.ConnectionId!.Value, stream, token);
        var asBytes = stream.ToArray();
        return asBytes;
    }

    public Task BroadcastContent(byte[] contentKey, byte[] value, CancellationToken token)
    {
        return _contentDistributor.DistributeContent(contentKey, value, token);
    }

    public async Task Run(CancellationToken token)
    {
        await _kademlia.Run(token);
    }

    public async Task Bootstrap(CancellationToken token)
    {
        await _kademlia.Bootstrap(token);
    }

    public void AddOrRefresh(IEnr node)
    {
        _kademlia.AddOrRefresh(node);
    }
}
