// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Adapter from TalkReq's ITalkReqProtocolHandler to Kademlia's IMessageReceiver, which is its incoming transport interface.
/// </summary>
/// <param name="kad"></param>
/// <param name="selfEnr"></param>
/// <param name="utpManager"></param>
public class KademliaTalkReqHandler(
    IPortalContentNetwork.Store store,
    IKademlia<IEnr, byte[], LookupContentResult> kad,
    IContentDistributor contentDistributor,
    IEnrProvider enrProvider,
    IUtpManager utpManager
) : ITalkReqProtocolHandler
{
    private readonly TimeSpan OfferAcceptTimeout = TimeSpan.FromSeconds(10);
    private readonly EnrNodeHashProvider _nodeHashProvider = EnrNodeHashProvider.Instance;

    public async Task<byte[]?> OnMsgReq(IEnr sender, TalkReqMessage talkReqMessage)
    {
        MessageUnion message = SlowSSZ.Deserialize<MessageUnion>(talkReqMessage.Request);

        if (message.Ping is { } ping)
        {
            return await HandlePing(sender, ping);
        }

        if (message.FindNodes is { } nodes)
        {
            return await HandleFindNode(sender, nodes);
        }

        if (message.FindContent is { } findContent)
        {
            return await HandleFindContent(sender, findContent);
        }

        if (message.Offer is { } offer)
        {
            return await HandleOffer(sender, offer);
        }

        return null;
    }

    private async Task<byte[]?> HandlePing(IEnr sender, Ping ping)
    {
        // Still need to call kad since the ping is also used to populate bucket.
        if (ping.CustomPayload?.Length == 32)
        {
            UInt256 radius = SlowSSZ.Deserialize<UInt256>(ping.CustomPayload);
            contentDistributor.UpdatePeerRadius(sender, radius);
        }

        await kad.Ping(sender, default);

        return SlowSSZ.Serialize(new MessageUnion()
        {
            Pong = new Pong()
            {
                EnrSeq = ping.EnrSeq,
                CustomPayload = ping.CustomPayload!
            }
        });
    }

    private async Task<byte[]?> HandleFindNode(IEnr sender, FindNodes nodes)
    {
        // So....
        // This is weird. For some reason, discv5/overlay network uses distance instead of target node like
        // with Kademlia/discv4. It also makes it more implementation specific.
        // fortunately, its basically the same as randomizing hash at a specific distance.
        // unfortunately, the protocol said to filter neighbour that is of incorrect distance...
        // which is another weird thing that I'm not sure how to handle.
        ValueHash256 theHash = Hash256XORUtils.GetRandomHashAtDistance(_nodeHashProvider.GetHash(enrProvider.SelfEnr), nodes.Distances[0]);
        var neighbours = await kad.FindNeighbours(sender, theHash, CancellationToken.None);
        var neighboursAsBytes = neighbours.Select<IEnr, byte[]>(ienr => ienr.EncodeRecord()).ToArray();

        var response = new MessageUnion()
        {
            Nodes = new Nodes()
            {
                Enrs = neighboursAsBytes
            }
        };

        return SlowSSZ.Serialize(response);
    }

    private async Task<byte[]?> HandleFindContent(IEnr sender, FindContent findContent)
    {
        var findValueResult = await kad.FindValue(sender, findContent.ContentKey, CancellationToken.None);
        if (findValueResult.hasValue)
        {
            // From the POV of Kademlia, there is no such thing as UTP. So when calling local kad,
            // it always return the full payload without any connection id.
            // But at transport/lantern/discv5 layer, we need to translate it to UTP, or it would be too big.
            LookupContentResult value = findValueResult.value!;
            if (value.Payload!.Length >  utpManager.MaxContentByteSize)
            {
                value.ConnectionId = InitiateUtpStreamSender(sender, value.Payload);
                value.Payload = null;
            }

            return SlowSSZ.Serialize(new MessageUnion()
            {
                Content = new Content()
                {
                    Payload = value.Payload,
                    ConnectionId = value.ConnectionId
                }
            });
        }

        var neighboursAsBytes = findValueResult.neighbours.Select<IEnr, byte[]>(ienr => ienr.EncodeRecord()).ToArray();
        var response = new MessageUnion()
        {
            Content = new Content()
            {
                Enrs = neighboursAsBytes
            }
        };

        return SlowSSZ.Serialize(response);
    }

    private ushort InitiateUtpStreamSender(IEnr nodeId, byte[] valuePayload)
    {
        ushort connectionId = (ushort)Random.Shared.Next();

        Task.Run(async () =>
        {
            // So we open a task that push the data.
            // But we cancel it after 10 second.
            // The peer will need to download it within 10 second.
            using CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            MemoryStream inputStream = new MemoryStream(valuePayload);
            await utpManager.WriteContentToUtp(nodeId, false, connectionId, inputStream, cts.Token);
        });

        return connectionId;
    }

    private Task<byte[]?> HandleOffer(IEnr sender, Offer offer)
    {
        if (offer.ContentKeys.Length == 0) return Task.FromResult<byte[]?>(null);
        BitArray toAccept = new BitArray(offer.ContentKeys.Length);
        bool hasAccept = false;
        for (var i = 0; i < toAccept.Count; i++)
        {
            if (store.ShouldAcceptOffer(offer.ContentKeys[i]))
            {
                toAccept[i] = true;
                hasAccept = true;
            }
        }

        if (!hasAccept) return Task.FromResult<byte[]?>(null);

        ushort connectionId = (ushort)Random.Shared.Next();
        _ = RunOfferAccept(sender, offer, toAccept, connectionId);

        return Task.FromResult<byte[]?>(SlowSSZ.Serialize(new MessageUnion()
        {
            Accept = new Accept()
            {
                ConnectionId = connectionId,
                AcceptedBits = toAccept
            }
        }));
    }

    private async Task RunOfferAccept(IEnr enr, Offer offer, BitArray accepted, ushort connectionId)
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        // cts.CancelAfter(OfferAcceptTimeout);
        var token =  cts.Token;

        MemoryStream stream = new MemoryStream(); // TODO: Must it wait for all of it to download?
        await utpManager.ReadContentFromUtp(enr, false, connectionId, stream, token);
        stream = new MemoryStream(stream.ToArray()); // Wait... how to use this exactly?

        for (var i = 0; i < offer.ContentKeys.Length; i++)
        {
            var contentKey = offer.ContentKeys[i];
            if (!accepted[i]) continue;

            ulong length = stream.ReadLEB128Unsigned();
            byte[] buffer = new byte[length];
            await stream.ReadAsync(buffer, token);

            store.Store(contentKey, buffer);
        }
    }
}
