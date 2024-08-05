// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

public class ContentDistributor : IContentDistributor
{
    private TimeSpan OfferAndSendContentTimeout = TimeSpan.FromSeconds(10);

    private readonly UInt256 _defaultRadius;
    private readonly EnrNodeHashProvider _nodeHashProvider = EnrNodeHashProvider.Instance;
    private readonly LruCache<ValueHash256, UInt256> _distanceCache = new(1000, "");
    private readonly IKademlia<IEnr, byte[], LookupContentResult> _kad;
    private readonly ITalkReqTransport _transport;
    private readonly IUtpManager _utpManager;
    private readonly IEnrProvider _enrProvider;
    private readonly ContentNetworkConfig _config;

    public ContentDistributor(
        IKademlia<IEnr, byte[], LookupContentResult> kad,
        IEnrProvider enrProvider,
        ContentNetworkConfig config,
        ITalkReqTransport transport,
        IUtpManager utpManager
    ) {
        _kad = kad;
        _config = config;
        _enrProvider = enrProvider;
        _transport = transport;
        _utpManager = utpManager;
        _defaultRadius = config.DefaultPeerRadius;
    }

    public void UpdatePeerRadius(IEnr node, UInt256 radius)
    {
        _distanceCache.Set(_nodeHashProvider.GetHash(node), radius);
    }

    private bool IsInRadius(IEnr node, ValueHash256 contentHash)
    {
        ValueHash256 nodeHash = new ValueHash256(node.NodeId);
        if (!_distanceCache.TryGet(nodeHash, out UInt256 nodeRadius))
        {
            nodeRadius = _defaultRadius;
        }

        return IsInRadius(nodeHash, contentHash, nodeRadius);
    }

    public bool IsContentInRadius(byte[] offerContentKey)
    {
        ValueHash256 contentHash = _nodeHashProvider.GetHash(offerContentKey);
        ValueHash256 nodeHash = _nodeHashProvider.GetHash(_enrProvider.SelfEnr);

        return IsInRadius(nodeHash, contentHash, _config.ContentRadius);
    }

    private bool IsInRadius(ValueHash256 nodeHash, ValueHash256 contentHash, UInt256 nodeRadius)
    {
        UInt256 distance = Hash256XORUtils.CalculateDistanceUInt256(contentHash, nodeHash);
        bool inRadius = distance <= nodeRadius;
        return inRadius;
    }

    public async Task DistributeContent(byte[] contentKey, byte[] content, CancellationToken token)
    {
        ValueHash256 contentHash = _nodeHashProvider.GetHash(contentKey);

        // Get list of nodes within the radius
        int nodeToDistributeTo = 8;
        SpanDictionary<byte, IEnr> nearestNodes = new(Bytes.SpanEqualityComparer);
        foreach (IEnr enr in _kad.IterateNeighbour(contentHash))
        {
            if (IsInRadius(enr, contentHash))
            {
                nearestNodes[enr.NodeId] = enr;
                if (nearestNodes.Count >= nodeToDistributeTo)
                    break;
            }
        }

        // Lookup n nearest node.
        // Note: It should be nearest node where its in radius.
        if (nearestNodes.Count < nodeToDistributeTo)
        {
            var enrs = await _kad.LookupNodesClosest(contentHash, nodeToDistributeTo, token);
            foreach (IEnr enr in enrs)
            {
                bool inRadius = IsInRadius(enr, contentHash);
                if (!nearestNodes.ContainsKey(enr.NodeId) && IsInRadius(enr, contentHash))
                {
                    nearestNodes[enr.NodeId] = enr;
                }
            }
        }

        foreach (KeyValuePair<byte[], IEnr> kv in nearestNodes)
        {
            try
            {
                IEnr enr = kv.Value;
                await OfferAndSendContent(enr, contentKey, content, token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task OfferAndSendContent(IEnr enr, byte[] contentKey, byte[] content, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(OfferAndSendContentTimeout);

        token = cts.Token;

        var message = SlowSSZ.Serialize(new MessageUnion()
        {
            Offer = new Offer()
            {
                ContentKeys = [contentKey]
            }
        });

        var responseByte = await _transport.CallAndWaitForResponse(enr, _config.ProtocolId, message, token);

        var accept = SlowSSZ.Deserialize<MessageUnion>(responseByte).Accept;
        if (accept == null) return;
        if (accept.AcceptedBits.Length == 0 || !accept.AcceptedBits[0]) return;

        MemoryStream toSendStream = new MemoryStream();
        toSendStream.WriteLEB128Unsigned((ulong)content.Length);
        toSendStream.Write(content);
        toSendStream = new MemoryStream(toSendStream.ToArray());

        await _utpManager.WriteContentToUtp(enr, true, accept.ConnectionId, toSendStream, token);
    }
}
