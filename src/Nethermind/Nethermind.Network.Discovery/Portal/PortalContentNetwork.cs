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
public class PortalContentNetwork : IPortalContentNetwork
{
    private readonly IKademlia<IEnr> _kademlia;
    private readonly IMessageSender<IEnr> _messageSender;
    private readonly IContentDistributor _contentDistributor;
    private readonly ILogger _logger1;
    private readonly ContentLookupService _contentLookupService;
    private readonly RadiusTracker _radiusTracker;

    public PortalContentNetwork(
        ContentNetworkConfig config,
        IKademlia<IEnr> kademlia,
        ITalkReqProtocolHandler talkReqHandler,
        ITalkReqTransport talkReqTransport,
        IMessageSender<IEnr> messageSender,
        IContentDistributor contentDistributor,
        RadiusTracker radiusTracker,
        ContentLookupService contentLookupService,
        ILogManager logManager)
    {
        talkReqTransport.RegisterProtocol(config.ProtocolId, talkReqHandler);
        _kademlia = kademlia;
        _messageSender = messageSender;
        _contentDistributor = contentDistributor;
        _logger1 = logManager.GetClassLogger<PortalContentNetwork>();
        _kademlia.OnNodeAdded += KademliaOnOnNodeAdded;
        _radiusTracker = radiusTracker;
        _contentLookupService = contentLookupService;

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
        if (!_radiusTracker.HasRadius(newNode))
        {
            if (_logger1.IsDebug) _logger1.Debug($"Ping {newNode.NodeId.ToHexString()} on new node");
            _messageSender.Ping(newNode, CancellationToken.None);
        }
    }

    public async Task<byte[]?> LookupContent(byte[] key, CancellationToken token)
    {
        (byte[]? Payload, bool UtpTransfer) contentLookup = await _contentLookupService.LookupContent(key, token);

        return contentLookup.Payload;
    }

    public async Task<byte[]?> LookupContentFrom(IEnr node, byte[] contentKey, CancellationToken token)
    {
        (byte[]? Payload, bool UtpTransfer, IEnr[]? Neighbours) contentLookup = await _contentLookupService.LookupContentFrom(node, contentKey, token);

        return contentLookup.Payload;
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
