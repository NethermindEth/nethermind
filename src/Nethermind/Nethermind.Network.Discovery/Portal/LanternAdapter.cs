// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.WireProtocol;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Packet;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Synchronization;
using NonBlocking;
using Org.BouncyCastle.Pqc.Crypto.Lms;

namespace Nethermind.Network.Discovery.Portal;

public class LanternAdapter: ILanternAdapter
{
    private readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(1);

    private readonly IDiscv5Protocol _discv5;
    private readonly ILogger _logger;
    private readonly IEnrDistanceCalculator _distanceCalculator = new IEnrDistanceCalculator();
    private readonly IPacketManager _packetManager;
    private readonly IMessageDecoder _messageDecoder;
    private readonly IIdentityVerifier _identityVerifier;
    private readonly IEnrFactory _enrFactory;

    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<TalkRespMessage>> _requestResp = new();
    private readonly Dictionary<byte[], IKademlia<IEnr, byte[]>> _kademliaOverlays = new();

    public LanternAdapter (
        IDiscv5Protocol discv5,
        IPacketManager packetManager,
        IMessageDecoder decoder,
        IIdentityVerifier identityVerifier,
        IEnrFactory enrFactory,
        ILogManager logManager
    )
    {
        _discv5 = discv5;
        _packetManager = packetManager;
        _messageDecoder = decoder;
        _identityVerifier = identityVerifier;
        _enrFactory = enrFactory;
        _logger = logManager.GetClassLogger<MsgReqManager>();
    }

    public Task<byte[]?> OnMsgReq(IEnr sender, TalkReqMessage talkReqMessage)
    {
        if (!_kademliaOverlays.TryGetValue(talkReqMessage.Protocol, out var kad))
        {
            _logger.Debug($"Unknown protocol {talkReqMessage.Protocol.ToHexString()}");
            return Task.FromResult<byte[]?>(null);
        }

        MessageUnion message = SlowSSZ.Deserialize<MessageUnion>(talkReqMessage.Request);

        if (message.Ping is { } ping)
        {
            MessageUnion pongMessage = new MessageUnion()
            {
                Pong = new Pong()
                {
                    EnrSeq = ping.EnrSeq,
                    CustomPayload = ping.CustomPayload
                }
            };

            return Task.FromResult((byte[]?)SlowSSZ.Serialize(pongMessage));
        }
        else if (message.FindNodes is { } nodes)
        {
            throw new NotImplementedException("not implemented");
            /*
            var neighbours = await kad.FindNeighbours(sender, nodes.Distances, CancellationToken.None);
            var neighboursAsBytes = neighbours.Select<IEnr, byte[]>(ienr => ienr.EncodeRecord()).ToArray();

            var response = new MessageUnion()
            {
                Nodes = new Nodes()
                {
                    Enrs = neighboursAsBytes
                }
            };

            return SlowSSZ.Serialize(response);
            */
        }
        else if (message.FindContent is { } findContent)
        {
            throw new NotImplementedException("not implemented");
            /*
            IEnr enrValue = new ContentEnr(findContent.ContentKey);
            var findValueResult = await kad.FindValue(sender, enrValue, CancellationToken.None);
            if (findValueResult.hasValue)
            {
                var value = findValueResult.value!;
                if (value.Length > 1000)
                {
                    throw new Exception("large value not supported yet");
                }

                return SlowSSZ.Serialize(new MessageUnion()
                {
                    Content = new Content()
                    {
                        Payload = findValueResult.value
                    }
                });
            }

            var neighboursAsBytes = neighbours.Select<IEnr, byte[]>(ienr => ienr.EncodeRecord()).ToArray();

            var response = new MessageUnion()
            {
                Nodes = new Nodes()
                {
                    Enrs = neighboursAsBytes
                }
            };

            return SlowSSZ.Serialize(response);
            */
        }

        return (Task<byte[]?>)Task.CompletedTask;
    }

    public void OnMsgResp(IEnr sender, TalkRespMessage message)
    {
        ulong requestId = BinaryPrimitives.ReadUInt64BigEndian(message.RequestId);
        if (_requestResp.TryRemove(requestId, out TaskCompletionSource<TalkRespMessage>? resp))
        {
            resp.TrySetResult(message);
            return;
        }

        _logger.Debug($"Unknown response for id {message.RequestId}");
    }

    public IMessageSender<IEnr, byte[]> CreateMessageSenderForProtocol(byte[] protocol)
    {
        return new KademliaMessageSender(protocol, this);
    }

    public void RegisterKademliaOverlay(byte[] protocol, IKademlia<IEnr, byte[]> kademlia)
    {
        _kademliaOverlays[protocol] = kademlia;
    }

    public async Task<byte[]> CallAndWaitForResponse(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(HardTimeout);

        TalkReqMessage talkReqMessage = await SentTalkReq(receiver, protocol, message);

        ulong requestId = BinaryPrimitives.ReadUInt64BigEndian(talkReqMessage.RequestId);

        try
        {
            var talkReq = new TaskCompletionSource<TalkRespMessage>(cts.Token);
            if (!_requestResp.TryAdd(requestId, talkReq))
            {
                return Array.Empty<byte>();
            }

            TalkRespMessage response = await talkReq.Task.WaitAsync(cts.Token);
            return response.Response;
        }
        finally
        {
            _requestResp.TryRemove(requestId, out _);
        }
    }

    private async Task<TalkReqMessage> SentTalkReq(IEnr receiver, byte[] protocol, byte[] message)
    {
        byte[] sentMessage = (await _packetManager.SendPacket(receiver, MessageType.TalkReq, false, protocol, message))!;
        // Yea... it does not return the original message, so we have to decode it to get the request id.
        // Either this or we have some more hacks.
        var talkReqMessage = (TalkReqMessage)_messageDecoder.DecodeMessage(sentMessage);
        return talkReqMessage;
    }

    private class KademliaMessageSender(byte[] protocol, LanternAdapter manager) : IMessageSender<IEnr, byte[]>
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

        public async Task<IEnr[]> FindNeighbours(IEnr receiver, IEnr hash, CancellationToken token)
        {
            // For some reason, the protocol uses the distance instead of the standard kademlia implementation
            // which checks for the k-nearest neighbour and just let the implementation decide how to implement it.
            // With the most basic implementation, this is the same as returning the bucket of the distance between
            // the target and current node. But more sophisticated routing table can do more if just query with
            // nodeid.
            ushort[] distances = new[]
            {
                (ushort)manager._distanceCalculator.CalculateDistance(receiver, manager._discv5.SelfEnr),
            };

            byte[] pingBytes =
                SlowSSZ.Serialize(new MessageUnion()
                {
                    FindNodes = new FindNodes()
                    {
                        Distances = distances
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

        public Task<FindValueResponse<IEnr, byte[]>> FindValue(IEnr receiver, IEnr hash, CancellationToken token)
        {
            throw new NotImplementedException();
        }
    }
}
