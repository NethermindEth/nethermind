// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.WireProtocol;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;
using Nethermind.Network.Discovery.UTP;
using NonBlocking;

namespace Nethermind.Network.Discovery.Portal;

public partial class LanternAdapter: ILanternAdapter
{
    private const int MaxContentByteSize = 1000;
    private static readonly byte[] UtpProtocolByte = Bytes.FromHexString("0x757470");

    private readonly TimeSpan HardTimeout = TimeSpan.FromMilliseconds(500);
    private readonly IDiscv5Protocol _discv5;
    private readonly ILogger _logger;
    private readonly ILogManager _logManager;
    private readonly EnrNodeHashProvider _nodeHashProvider = new EnrNodeHashProvider();
    private readonly IPacketManager _packetManager;
    private readonly IMessageDecoder _messageDecoder;
    private readonly IIdentityVerifier _identityVerifier;
    private readonly IEnrFactory _enrFactory;
    private readonly IRoutingTable _routingTable;
    private readonly IRequestManager _requestManager;
    private readonly ISessionManager _sessionManager;
    private readonly IIdentityManager _identityManager;

    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<TalkRespMessage>> _requestResp = new();
    private readonly SpanLruCache<byte, bool> _respSilencer = new SpanLruCache<byte, bool>(10, 1, "", Bytes.SpanEqualityComparer);

    private readonly SpanDictionary<byte, IKademlia<IEnr, byte[], LookupContentResult>> _kademliaOverlays = new(Bytes.SpanEqualityComparer);
    // TODO: Maybe use Lru?
    private readonly ConcurrentDictionary<(IEnr, ushort), UTPStream> _utpStreams = new();

    public LanternAdapter (
        IDiscv5Protocol discv5,
        IPacketManager packetManager,
        IMessageDecoder decoder,
        IIdentityVerifier identityVerifier,
        IIdentityManager identityManager,
        IEnrFactory enrFactory,
        IRoutingTable routingTable,
        IRequestManager requestManager,
        ISessionManager sessionManager,
        ILogManager logManager
    )
    {
        _discv5 = discv5;
        _packetManager = packetManager;
        _messageDecoder = decoder;
        _identityVerifier = identityVerifier;
        _enrFactory = enrFactory;
        _routingTable = routingTable;
        _requestManager = requestManager;
        _sessionManager = sessionManager;
        _identityManager = identityManager;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<LanternAdapter>();
    }

    public async Task<byte[]?> OnMsgReq(IEnr sender, TalkReqMessage talkReqMessage)
    {
        if (Bytes.AreEqual(talkReqMessage.Protocol, UtpProtocolByte))
        {
            return await HandleUtpMessage(sender, talkReqMessage);
        }

        if (!_kademliaOverlays.TryGetValue(talkReqMessage.Protocol, out var kad))
        {
            _logger.Info($"Unknown msg req for protocol {talkReqMessage.Protocol.ToHexString()}");
            return null;
        }

        MessageUnion message = SlowSSZ.Deserialize<MessageUnion>(talkReqMessage.Request);

        if (message.Ping is { } ping)
        {
            return await HandlePing(sender, kad, ping);
        }

        if (message.FindNodes is { } nodes)
        {
            return await HandleFindNode(sender, nodes, kad);
        }

        if (message.FindContent is { } findContent)
        {
            return await HandleFindContent(sender, kad, findContent);
        }

        return null;
    }

    private async Task<byte[]?> HandleUtpMessage(IEnr sender, TalkReqMessage talkReqMessage)
    {
        (UTPPacketHeader header, int headerSize) = UTPPacketHeader.DecodePacket(talkReqMessage.Request);
        _logger.Info($"Handle utp message from :{header.ConnectionId} {header}");
        if (!_utpStreams.TryGetValue((sender, header.ConnectionId), out UTPStream? stream))
        {
            _logger.Info($"Unknown connection id");
            return null;
        }

        await stream.ReceiveMessage(header, talkReqMessage.Request.AsSpan()[headerSize..], CancellationToken.None);
        return Array.Empty<byte>();
    }

    private static async Task<byte[]?> HandlePing(IEnr sender, IKademlia<IEnr, byte[], LookupContentResult> kad, Ping ping)
    {
        // Still need to call kad since the ping is also used to populate bucket.
        await kad.Ping(sender, default);

        return SlowSSZ.Serialize(new MessageUnion()
        {
            Pong = new Pong()
            {
                EnrSeq = ping.EnrSeq,
                CustomPayload = ping.CustomPayload
            }
        });
    }

    private async Task<byte[]?> HandleFindNode(IEnr sender, FindNodes nodes, IKademlia<IEnr, byte[], LookupContentResult> kad)
    {
        // So....
        // This is weird. For some reason, discv5/overlay network uses distance instead of target node like
        // with Kademlia/discv4. It also makes it more implementation specific.
        // fortunately, its basically the same as randomizing hash at a specific distance.
        // unfortunately, the protocol said to filter neighbour that is of incorrect distance...
        // which is another weird thing that I'm not sure how to handle.
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

    private async Task<byte[]?> HandleFindContent(IEnr sender, IKademlia<IEnr, byte[], LookupContentResult> kad, FindContent findContent)
    {
        var findValueResult = await kad.FindValue(sender, findContent.ContentKey, CancellationToken.None);
        if (findValueResult.hasValue)
        {
            // From the POV of Kademlia, there is no such thing as UTP. So when calling local kad,
            // it always return the full payload without any connection id.
            // But at transport/lantern/discv5 layer, we need to translate it to UTP, or it would be too big.
            LookupContentResult value = findValueResult.value!;
            if (value.Payload!.Length > MaxContentByteSize)
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
            Nodes = new Nodes()
            {
                Enrs = neighboursAsBytes
            }
        };

        return SlowSSZ.Serialize(response);
    }

    public void OnMsgResp(IEnr sender, TalkRespMessage message)
    {
        _requestManager.MarkRequestAsFulfilled(message.RequestId);

        ulong requestId = BinaryPrimitives.ReadUInt64BigEndian(message.RequestId);
        if (_requestResp.TryRemove(requestId, out TaskCompletionSource<TalkRespMessage>? resp))
        {
            if (_logger.IsTrace) _logger.Trace($"TalkResp {message.RequestId.ToHexString()} fulfilled");
            resp.TrySetResult(message);
            return;
        }

        if (_respSilencer.TryGet(message.RequestId, out _)) return;

        if (_logger.IsTrace) _logger.Trace($"TalkResp {message.RequestId.ToHexString()} failed no mapping. {message.Response.ToHexString()}");
    }

    public IMessageSender<IEnr, byte[], LookupContentResult> CreateMessageSenderForProtocol(byte[] protocol)
    {
        return new KademliaMessageSender(protocol, this);
    }

    public async Task<byte[]> CallAndWaitForResponse(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(HardTimeout);

        TalkReqMessage talkReqMessage = await SentTalkReq(receiver, protocol, message, token);
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

    private async Task<TalkReqMessage> SentTalkReq(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token)
    {
        // Needed as its possible that the routing table does not
        _routingTable.UpdateFromEnr(receiver);

        var destIpKey = receiver.GetEntry<EntryIp>(EnrEntryKey.Ip).Value;
        var nodeId = receiver.NodeId.ToHexString();

        byte[]? sentMessage = null;
        // So sentMessage can be null if there is an ongoing handshake that is not completed yet.
        // So what happen here is that, if the receiver does not have any session yet, lantern will create a "cached"
        // packet which will be used as a reply to a WHOAREYOU message.
        // BUT if the are already a "cached" request, lantern will straight up fail to send a message and return null. (TODO: double check this)
        do
        {
            token.ThrowIfCancellationRequested();
            sentMessage = (await _packetManager.SendPacket(receiver, MessageType.TalkReq, false, protocol, message));
            if (sentMessage == null)
            {
                // Well.... got another idea?
                await Task.Delay(100, token);
            }
        } while (sentMessage == null);

        // Yea... it does not return the original message, so we have to decode it to get the request id.
        // Either this or we have some more hacks.
        var talkReqMessage = (TalkReqMessage)_messageDecoder.DecodeMessage(sentMessage);
        if (_logger.IsDebug) _logger.Debug($"Sent TalkReq to {destIpKey}, {nodeId} with request id {talkReqMessage.RequestId.ToHexString()}");
        return talkReqMessage;
    }

    private async Task<TalkReqMessage> SentTalkReqAndSilenceResp(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token)
    {
        TalkReqMessage req = await SentTalkReq(receiver, protocol, message, token);
        _respSilencer.Set(req.RequestId, true);
        return req;
    }

    private async Task<byte[]?> DownloadContentFromUtp(IEnr nodeId, ushort connectionId, CancellationToken token)
    {
        byte[] asByte = new byte[2];

        // TODO: Ah man... where did this go wrong.
        BinaryPrimitives.WriteUInt16LittleEndian(asByte, connectionId);
        connectionId = BinaryPrimitives.ReadUInt16BigEndian(asByte);

        if (_logger.IsDebug) _logger.Debug($"Downloading UTP content from {nodeId} with connection id {connectionId}");

        // Now, UTP have this strange connection id mechanism where the initiator will initiate with starting connection id
        // BUT after the Syn, it send with that connection id + 1.
        ushort otherSideConnectionId = (ushort)(connectionId + 1);
        UTPStream stream = new UTPStream(new UTPToMsgReqAdapter(nodeId, this), otherSideConnectionId, _logManager);
        if (!_utpStreams.TryAdd((nodeId, (ushort)(connectionId)), stream))
        {
            throw new Exception("Unable to open utp stream. Connection id may already be used.");
        }

        try
        {
            MemoryStream outputStream = new MemoryStream();
            await stream.InitiateHandshake(token, synConnectionId: connectionId);
            await stream.ReadStream(outputStream, token);
            return outputStream.ToArray();
        }
        finally
        {
            _utpStreams.Remove((nodeId, connectionId), out _);
        }
    }

    private class UTPToMsgReqAdapter(IEnr targetNode, LanternAdapter lanternAdapter): IUTPTransfer
    {
        public Task ReceiveMessage(UTPPacketHeader meta, ReadOnlySpan<byte> data, CancellationToken token)
        {
            var dataArray = UTPPacketHeader.EncodePacket(meta, data, new byte[2047]).ToArray();
            lanternAdapter._logger.Info($"Sending utp message to {meta.ConnectionId} {meta}.");
            return lanternAdapter.SentTalkReqAndSilenceResp(targetNode, UtpProtocolByte, dataArray, token);
        }
    }

    private ushort? InitiateUtpStreamSender(IEnr sender, byte[] valuePayload)
    {
        throw new NotImplementedException();
    }

    public IPortalContentNetwork RegisterContentNetwork(byte[] protocol, IPortalContentNetwork.Store store)
    {
        _logger.Warn($"self enode is {_discv5.SelfEnr.NodeId.ToHexString()}");
        var messageSender = CreateMessageSenderForProtocol(protocol);

        var kademlia = new Kademlia<IEnr, byte[], LookupContentResult>(
            _nodeHashProvider,
            new PortalContentStoreAdapter(store),
            messageSender,
            _logManager,
            _discv5.SelfEnr,
            20,
            3,
            TimeSpan.FromHours(1)
        );
        _kademliaOverlays[protocol] = kademlia;

        return new PortalContentNetwork(this, kademlia, messageSender, _logger);
    }
}
