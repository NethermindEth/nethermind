// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO.Pipelines;
using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Kademlia.Content;
using Nethermind.Network.Discovery.Portal.Messages;
using Nethermind.Serialization;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Adapter from TalkReq's ITalkReqProtocolHandler to Kademlia's IMessageReceiver, which is its incoming transport interface.
/// </summary>
/// <param name="kad"></param>
/// <param name="selfEnr"></param>
/// <param name="utpManager"></param>
public class TalkReqHandler(
    IPortalContentNetworkStore store,
    IKademliaMessageReceiver<IEnr> kadKademliaMessageReceiver,
    IContentMessageReceiver<IEnr, byte[], LookupContentResult> kadContentMessageReceiver,
    RadiusTracker radiusTracker,
    IEnrProvider enrProvider,
    IUtpManager utpManager,
    ContentNetworkConfig config,
    ILogManager logManager
) : ITalkReqProtocolHandler
{
    private readonly ILogger _logger = logManager.GetClassLogger<TalkReqHandler>();

    private readonly TimeSpan _offerAcceptTimeout = config.OfferAcceptTimeout;

    private readonly TimeSpan _streamSenderTimeout = config.StreamSenderTimeout;

    private readonly EnrNodeHashProvider _nodeHashProvider = EnrNodeHashProvider.Instance;

    public async Task<byte[]?> OnMsgReq(IEnr sender, TalkReqMessage talkReqMessage)
    {
        try
        {
            SszEncoding.Decode(talkReqMessage.Request, out MessageUnion message);

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
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Error handling msg req {e}");
        }

        return null;
    }

    private async Task<byte[]?> HandlePing(IEnr sender, Ping ping)
    {
        if (_logger.IsDebug) _logger.Debug($"Handling ping from {sender.NodeId.ToHexString()}");

        // Still need to call kadMessageReceiver since the ping is also used to populate bucket.
        if (ping.CustomPayload?.Length == 32)
        {
            UInt256 radius = new UInt256(ping.CustomPayload, isBigEndian: false);
            radiusTracker.UpdatePeerRadius(sender, radius);
        }

        await kadKademliaMessageReceiver.Ping(sender, default);

        return SszEncoding.Encode(new MessageUnion()
        {
            Selector = MessageType.Pong,
            Pong = new Pong()
            {
                EnrSeq = ping.EnrSeq,
                CustomPayload = config.ContentRadius.ToLittleEndian()
            }
        });
    }

    private async Task<byte[]?> HandleFindNode(IEnr sender, FindNodes nodes)
    {
        if (_logger.IsDebug) _logger.Debug($"Handling find node from {sender.NodeId.ToHexString()}");
        // So....
        // This is weird. For some reason, discv5/overlay network uses distance instead of target node like
        // with Kademlia/discv4. It also makes it more implementation specific.
        // fortunately, its basically the same as randomizing hash at a specific distance.
        // unfortunately, the protocol said to filter neighbour that is of incorrect distance...
        // which is another weird thing that I'm not sure how to handle.
        ValueHash256 theHash = Hash256XorUtils.GetRandomHashAtDistance(_nodeHashProvider.GetHash(enrProvider.SelfEnr), nodes.Distances[0]);
        var neighbours = await kadKademliaMessageReceiver.FindNeighbours(sender, theHash, CancellationToken.None);
        var neighboursAsBytes = neighbours.Select<IEnr, byte[]>(ienr => ienr.EncodeRecord()).ToArray();

        var response = new MessageUnion()
        {
            Selector = MessageType.Nodes,
            Nodes = new Nodes()
            {
                Enrs = neighboursAsBytes.Select(enr => new Messages.Enr { Data = enr }).ToArray()
            }
        };

        return SszEncoding.Encode(response);
    }

    private async Task<byte[]?> HandleFindContent(IEnr sender, FindContent findContent)
    {
        if (_logger.IsDebug) _logger.Debug($"Handling find content from {sender.NodeId.ToHexString()}");
        var findValueResult = await kadContentMessageReceiver.FindValue(sender, findContent.ContentKey.Data, CancellationToken.None);
        if (findValueResult.HasValue)
        {
            // From the POV of Kademlia, there is no such thing as UTP. So when calling local kad,
            // it always return the full payload without any connection id.
            // But at transport/lantern/discv5 layer, we need to translate it to UTP, or it would be too big.
            LookupContentResult value = findValueResult.Value!;
            if (value.Payload!.Length > config.MaxContentSizeForTalkReq)
            {
                return SszEncoding.Encode(new MessageUnion()
                {
                    Selector = MessageType.Content,
                    Content = new Content()
                    {
                        Selector = ContentType.ConnectionId,
                        ConnectionId = InitiateUtpStreamSender(sender, value.Payload),
                    }
                });
            }

            return SszEncoding.Encode(new MessageUnion()
            {
                Selector = MessageType.Content,
                Content = new Content()
                {
                    Selector = ContentType.Payload,
                    Payload = value.Payload,
                }
            });
        }

        var neighboursAsBytes = findValueResult.Neighbours.Select<IEnr, byte[]>(ienr => ienr.EncodeRecord()).ToArray();
        var response = new MessageUnion()
        {
            Selector = MessageType.Content,
            Content = new Content()
            {
                Selector = ContentType.Enrs,
                Enrs = neighboursAsBytes.Select(enr => new Messages.Enr { Data = enr }).ToArray()
            }
        };

        return SszEncoding.Encode(response);
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
            cts.CancelAfter(_streamSenderTimeout);

            MemoryStream inputStream = new MemoryStream(valuePayload);
            await utpManager.WriteContentToUtp(nodeId, false, connectionId, inputStream, cts.Token);
        });

        return connectionId;
    }

    private Task<byte[]?> HandleOffer(IEnr sender, Offer offer)
    {
        if (_logger.IsDebug) _logger.Debug($"Handling offer from {sender.NodeId.ToHexString()}");

        if (offer.ContentKeys.Length == 0)
        {
            if (_logger.IsDebug) _logger.Debug($"Empty content keys");
            return Task.FromResult<byte[]?>(null);
        }
        BitArray toAccept = new BitArray(offer.ContentKeys.Length);
        bool hasAccept = false;
        for (var i = 0; i < toAccept.Count; i++)
        {
            if (!radiusTracker.IsContentInRadius(offer.ContentKeys[i].Data))
            {
                if (_logger.IsTrace) _logger.Trace($"Content {offer.ContentKeys[i].Data.ToHexString()} not in radius");
                continue;
            }
            if (store.ShouldAcceptOffer(offer.ContentKeys[i].Data))
            {
                toAccept[i] = true;
                hasAccept = true;
            }
        }

        if (!hasAccept)
        {
            if (_logger.IsDebug) _logger.Debug($"No content accepted");
            return Task.FromResult<byte[]?>(null);
        }

        if (_logger.IsDebug) _logger.Debug($"Accepting offer from {sender.NodeId.ToHexString()}");
        ushort connectionId = (ushort)Random.Shared.Next();
        _ = RunOfferAccept(sender, offer, toAccept, connectionId);

        return Task.FromResult<byte[]?>(SszEncoding.Encode(new MessageUnion()
        {
            Selector = MessageType.Accept,
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
        cts.CancelAfter(_offerAcceptTimeout);
        var token =  cts.Token;

        var pipe = new Pipe();
        var writeTask = utpManager.ReadContentFromUtp(enr, false, connectionId, pipe.Writer.AsStream(), token);
        var readTask = Task.Run(async () =>
        {
            var stream = pipe.Reader.AsStream();

            for (var i = 0; i < offer.ContentKeys.Length; i++)
            {
                var contentKey = offer.ContentKeys[i];
                if (!accepted[i]) continue;

                ulong length = stream.ReadLEB128Unsigned();
                byte[] buffer = new byte[length];
                await stream.ReadExactlyAsync(buffer, token);

                store.Store(contentKey.Data, buffer);
            }
        }, token);

        await Task.WhenAll(writeTask, readTask);
    }
}
