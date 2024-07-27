// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.WireProtocol;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Table;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using NonBlocking;

namespace Nethermind.Network.Discovery.Portal;

public class LanternAdapter: ILanternAdapter
{
    private const int MaxContentByteSize = 1000;

    private readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(2);
    private readonly IDiscv5Protocol _discv5;
    private readonly ILogger _logger;
    private readonly EnrNodeHashProvider _nodeHashProvider = new EnrNodeHashProvider();
    private readonly IPacketManager _packetManager;
    private readonly IMessageDecoder _messageDecoder;
    private readonly IIdentityVerifier _identityVerifier;
    private readonly IEnrFactory _enrFactory;
    private readonly IRoutingTable _routingTable;
    private PortalContentEncoderDecoder _contentEncoder = new PortalContentEncoderDecoder();

    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<TalkRespMessage>> _requestResp = new();
    private readonly SpanDictionary<byte, IKademlia<IEnr, ContentKey, ContentContent>> _kademliaOverlays = new(Bytes.SpanEqualityComparer);

    public LanternAdapter (
        IDiscv5Protocol discv5,
        IPacketManager packetManager,
        IMessageDecoder decoder,
        IIdentityVerifier identityVerifier,
        IEnrFactory enrFactory,
        IRoutingTable routingTable,
        ILogManager logManager
    )
    {
        _discv5 = discv5;
        _packetManager = packetManager;
        _messageDecoder = decoder;
        _identityVerifier = identityVerifier;
        _enrFactory = enrFactory;
        _routingTable = routingTable;
        _logger = logManager.GetClassLogger<DiscV5Overlay>();
    }

    public async Task<byte[]?> OnMsgReq(IEnr sender, TalkReqMessage talkReqMessage)
    {
        if (!_kademliaOverlays.TryGetValue(talkReqMessage.Protocol, out var kad))
        {
            _logger.Info($"Unknown msg req for protocol {talkReqMessage.Protocol.ToHexString()}");
            return null;
        }

        _logger.Info($"Got msg req on protocol {talkReqMessage.Protocol.ToHexString()}");
        MessageUnion message = SlowSSZ.Deserialize<MessageUnion>(talkReqMessage.Request);

        if (message.Ping is { } ping)
        {
            // Still need to call kad since the ping is also used to populate bucket.
            await kad.Ping(sender, default);

            MessageUnion pongMessage = new MessageUnion()
            {
                Pong = new Pong()
                {
                    EnrSeq = ping.EnrSeq,
                    CustomPayload = ping.CustomPayload
                }
            };

            return SlowSSZ.Serialize(pongMessage);
        }
        else if (message.FindNodes is { } nodes)
        {
            // So....
            // This is weird. For some reason, discv5/overlay network uses distance instead of target node like
            // with Kademlia/discv4. It also makes it more implementation specific.
            // fortunately, its basically the same as randomizing hash at a specific distance.
            ValueHash256 theHash = Hash256XORUtils.RandomizeHashAtDistance(_nodeHashProvider.GetHash(_discv5.SelfEnr), nodes.Distances[0]);
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
        else if (message.FindContent is { } findContent)
        {
            // TODO: Straight up wrong
            var findValueResult = await kad.FindValue(sender, findContent.ContentKey, CancellationToken.None);
            if (findValueResult.hasValue)
            {
                byte[] thePayload = _contentEncoder.Encode(findValueResult.value!);
                if (thePayload.Length > MaxContentByteSize)
                {
                    throw new NotImplementedException("large value not supported yet");
                }

                return SlowSSZ.Serialize(new MessageUnion()
                {
                    Content = new Content()
                    {
                        Payload = thePayload
                    }
                });
            }

            var neighboursAsBytes = findValueResult.neighbours.Select<IEnr, byte[]>(ienr => ienr.EncodeRecord()).ToArray();

            var response = new MessageUnion()
            {
                Nodes = new Nodes()
                {
                    Enrs = neighboursAsBytes
                }
            };

            return SlowSSZ.Serialize(response);
        }

        return null;
    }

    public void OnMsgResp(IEnr sender, TalkRespMessage message)
    {
        try
        {
            ulong requestId = BinaryPrimitives.ReadUInt64BigEndian(message.RequestId);
            if (_requestResp.TryRemove(requestId, out TaskCompletionSource<TalkRespMessage>? resp))
            {
                _logger.Info($"Msgresp {message.RequestId.ToHexString()} fulfilled");
                resp.TrySetResult(message);
                return;
            }

            _logger.Info($"Msgresp {message.RequestId.ToHexString()} failed no mapping");
        }
        catch (Exception e)
        {
            _logger.Error($"Msgresp {message.RequestId.ToHexString()} failed with exception", e);
        }
    }

    public IMessageSender<IEnr, ContentKey, ContentContent> CreateMessageSenderForProtocol(byte[] protocol)
    {
        return new KademliaMessageSender(protocol, this);
    }

    public void RegisterKademliaOverlay(byte[] protocol, IKademlia<IEnr, ContentKey, ContentContent> kademlia)
    {
        _kademliaOverlays[protocol] = kademlia;
    }

    public class InHandshakeException() : Exception;

    public async Task<byte[]> CallAndWaitForResponse(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(HardTimeout);

        TalkReqMessage? talkReqMessage = await SentTalkReq(receiver, protocol, message);
        if (talkReqMessage == null)
        {
            // So what happen here is that, if the receiver does not have any session yet, lantern will create a "cached"
            // packet which will be used as a reply to a WHOAREYOU message.
            // BUT if the are already a "cached" request, lantern will straight up fail to send a message. (TODO: double check this)
            throw new InHandshakeException();
        }

        ulong requestId = BinaryPrimitives.ReadUInt64BigEndian(talkReqMessage.RequestId);

        try
        {
            var talkReq = new TaskCompletionSource<TalkRespMessage>(cts.Token);
            if (!_requestResp.TryAdd(requestId, talkReq))
            {
                return Array.Empty<byte>();
            }

            TalkRespMessage response = await talkReq.Task.WaitAsync(cts.Token);
            _logger.Info($"Received TalkResp from {receiver}");
            return response.Response;
        }
        catch (OperationCanceledException)
        {
            var destIpKey = receiver.GetEntry<EntryIp>(EnrEntryKey.Ip).Value;
            _logger.Warn($"TalkResp to {destIpKey} with id {requestId} timed out");
            throw;
        }
        finally
        {
            _requestResp.TryRemove(requestId, out _);
        }
    }

    private async Task<TalkReqMessage?> SentTalkReq(IEnr receiver, byte[] protocol, byte[] message)
    {
        _routingTable.UpdateFromEnr(receiver);

        var destIpKey = receiver.GetEntry<EntryIp>(EnrEntryKey.Ip).Value;
        var nodeId = receiver.NodeId.ToHexString();
        _logger.Info($"Send TalkReq to {destIpKey}, {nodeId}");

        byte[]? sentMessage = (await _packetManager.SendPacket(receiver, MessageType.TalkReq, false, protocol, message))!;
        if (sentMessage == null)
        {
            _logger.Warn("Talkreq does not get a response");
            return null;
        }

        // Yea... it does not return the original message, so we have to decode it to get the request id.
        // Either this or we have some more hacks.
        var talkReqMessage = (TalkReqMessage)_messageDecoder.DecodeMessage(sentMessage);
        _logger.Info($"Send talkreq to {destIpKey}, {nodeId} with request id {talkReqMessage.RequestId.ToHexString()}");
        return talkReqMessage;
    }

    private class KademliaMessageSender(byte[] protocol, LanternAdapter manager) : IMessageSender<IEnr, ContentKey, ContentContent>
    {
        public async Task Ping(IEnr receiver, CancellationToken token)
        {

            byte[] pingBytes =
                SlowSSZ.Serialize(new MessageUnion()
                {
                    Ping = new Ping()
                    {
                        EnrSeq = manager._discv5.SelfEnr.SequenceNumber,
                        CustomPayload = Array.Empty<byte>()
                    }
                });

            await manager.CallAndWaitForResponse(receiver, protocol, pingBytes, token);
        }

        public async Task<IEnr[]> FindNeighbours(IEnr receiver, ValueHash256 hash, CancellationToken token)
        {
            // For some reason, the protocol uses the distance instead of the standard kademlia RPC
            // which checks for the k-nearest nodes and just let the implementation decide how to implement it.
            // With the most basic implementation, this is the same as returning the bucket of the distance between
            // the target and current node. But more sophisticated routing table can do more if just query with
            // nodeid.
            ushort theDistance = (ushort)Hash256XORUtils.CalculateDistance(manager._nodeHashProvider.GetHash(receiver), hash);

            // To simulate a neighbour query to a particular hash with distance, we also query for neighbouring
            // bucket in the order as if we are running a query to a particular hash
            int toAddExtra = 4;
            ushort[] queryDistance = new ushort[1 + toAddExtra];
            bool nowUpper = true;
            ushort upper = theDistance;
            ushort lower = theDistance;
            queryDistance[0] = theDistance;
            for (int i = 0; i < toAddExtra; i++)
            {
                if (nowUpper && upper < 255)
                {
                    upper++;
                    queryDistance[i+1] = upper;
                    nowUpper = false;
                }
                else if (lower > 0)
                {
                    lower--;
                    queryDistance[i+1] = lower;
                    nowUpper = true;
                }
            }

            byte[] pingBytes = SlowSSZ.Serialize(new MessageUnion()
            {
                FindNodes = new FindNodes()
                {
                    Distances = queryDistance
                }
            });

            byte[] response = await manager.CallAndWaitForResponse(receiver, protocol, pingBytes, token);

            Nodes message = SlowSSZ.Deserialize<MessageUnion>(response).Nodes!;

            IEnr[] enrs = new IEnr[message.Enrs.Length];

            for (var i = 0; i < message.Enrs.Length; i++)
            {
                enrs[i] = manager._enrFactory.CreateFromBytes(message.Enrs[i], manager._identityVerifier);
            }

            return enrs;
        }

        public async Task<FindValueResponse<IEnr, ContentContent>> FindValue(IEnr receiver, ContentKey contentKey, CancellationToken token)
        {
            byte[] findContentBytes = SlowSSZ.Serialize(new MessageUnion()
            {
                FindContent = new FindContent()
                {
                    ContentKey = contentKey
                }
            });

            byte[] response = await manager.CallAndWaitForResponse(receiver, protocol, findContentBytes, token);
            Content message = SlowSSZ.Deserialize<MessageUnion>(response).Content!;

            if (message.ConnectionId == null && message.Payload == null)
            {
                IEnr[] enrs = new IEnr[message.Enrs!.Length];
                for (var i = 0; i < message.Enrs.Length; i++)
                {
                    enrs[i] = manager._enrFactory.CreateFromBytes(message.Enrs[i], manager._identityVerifier);
                }

                return new FindValueResponse<IEnr, ContentContent>(false, null, enrs);
            }

            byte[] payload;
            if (message.Payload != null)
            {
                payload = message.Payload!;
            }
            else
            {
                throw new NotImplementedException($"UTP connection id not implemented. Got id {message.ConnectionId}");
            }

            return new FindValueResponse<IEnr, ContentContent>(true, manager._contentEncoder.Decode(contentKey, payload), Array.Empty<IEnr>());
        }
    }
}
