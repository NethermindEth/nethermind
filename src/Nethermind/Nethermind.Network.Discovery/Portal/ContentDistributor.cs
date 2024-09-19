// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.IO.Pipelines;
using Lantern.Discv5.Enr;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

public class ContentDistributor(
    IKademlia<IEnr> kad,
    RadiusTracker radiusTracker,
    ContentNetworkConfig config,
    IContentNetworkProtocol protocol,
    IUtpManager utpManager)
    : IContentDistributor
{
    private readonly ContentKeyHashProvider _contentKeyHashProvider = ContentKeyHashProvider.Instance;
    private readonly TimeSpan _offerAndSendContentTimeout = config.OfferAndSendContentTimeout;

    public async Task<int> DistributeContent(byte[] contentKey, byte[] content, CancellationToken token)
    {
        ValueHash256 contentHash = _contentKeyHashProvider.GetHash(contentKey);

        // Get list of nodes within the radius
        int nodeToDistributeTo = 8;
        SpanDictionary<byte, IEnr> nearestNodes = new(Bytes.SpanEqualityComparer);
        foreach (IEnr enr in kad.GetKNeighbour(contentHash, null))
        {
            if (radiusTracker.IsInRadius(enr, contentHash))
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
            var enrs = await kad.LookupNodesClosest(contentHash, token, nodeToDistributeTo);
            foreach (IEnr enr in enrs)
            {
                bool inRadius = radiusTracker.IsInRadius(enr, contentHash);
                if (!nearestNodes.ContainsKey(enr.NodeId) && inRadius)
                {
                    nearestNodes[enr.NodeId] = enr;
                }
            }
        }

        int peerDistributed = 0;
        foreach (KeyValuePair<byte[], IEnr> kv in nearestNodes)
        {
            try
            {
                // TODO: Parallelize
                IEnr enr = kv.Value;
                await OfferAndSendContent(enr, contentKey, content, token);
                peerDistributed++;
            }
            catch (OperationCanceledException)
            {
            }
        }

        return peerDistributed;
    }

    public async Task OfferAndSendContent(IEnr enr, byte[] contentKey, byte[] content, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(_offerAndSendContentTimeout);

        token = cts.Token;

        Accept accept = await protocol.Offer(enr, new Offer()
        {
            ContentKeys = [contentKey]
        }, token);

        if (accept.AcceptedBits.Length == 0 || !accept.AcceptedBits[0]) return;

        var pipe = new Pipe();
        var writeTask = Task.Run(() =>
        {
            Stream stream = pipe.Writer.AsStream();
            stream.WriteLEB128Unsigned((ulong)content.Length);
            stream.Write(content);
            stream.Close();
            pipe.Writer.Complete();
        }, cts.Token);

        Task readTask = utpManager.WriteContentToUtp(enr, true, accept.ConnectionId, pipe.Reader.AsStream(), token);
        await Task.WhenAll(writeTask, readTask);
    }
}
