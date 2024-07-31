// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Table;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using NonBlocking;

namespace Nethermind.Network.Discovery.Portal;

public class LanternTalkReqTransport(
    IRoutingTable routingTable,
    IPacketManager packetManager,
    IMessageDecoder messageDecoder,
    IRequestManager requestManager,
    ILogManager logManager
): ITalkReqTransport
{
    private ILogger _logger = logManager.GetClassLogger<LanternTalkReqTransport>();
    private readonly TimeSpan HardCallTimeout = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<TalkRespMessage>> _requestResp = new();
    private readonly SpanDictionary<byte, ITalkReqProtocolHandler> _protocolHandlers = new(Bytes.SpanEqualityComparer);

    public async Task<byte[]?> OnMsgReq(IEnr sender, TalkReqMessage message)
    {
        if (!_protocolHandlers.TryGetValue(message.Protocol, out var handler))
        {
            if (_logger.IsDebug) _logger.Debug($"Unknown msg req for protocol {message.Protocol.ToHexString()}");
            return null;
        }

        return await handler.OnMsgReq(sender, message);
    }

    public void OnTalkResp(IEnr sender, TalkRespMessage message)
    {
        // So that it wont disconnect the peer
        requestManager.MarkRequestAsFulfilled(message.RequestId);

        ulong requestId = BinaryPrimitives.ReadUInt64BigEndian(message.RequestId);
        if (_requestResp.TryRemove(requestId, out TaskCompletionSource<TalkRespMessage>? resp))
        {
            if (_logger.IsTrace) _logger.Trace($"TalkResp {message.RequestId.ToHexString()} fulfilled");
            resp.TrySetResult(message);
            return;
        }

        // Note: It could just be `SentTalkReq` which does not wait for response.
        if (_logger.IsTrace) _logger.Trace($"TalkResp {message.RequestId.ToHexString()} failed no mapping. {message.Response.ToHexString()}");
    }

    public void RegisterProtocol(byte[] protocol, ITalkReqProtocolHandler protocolHandler)
    {
        _protocolHandlers[protocol] = protocolHandler;
    }

    public async Task<TalkReqMessage> SentTalkReq(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token)
    {
        // Needed as its possible that the routing table does not
        routingTable.UpdateFromEnr(receiver);

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
            sentMessage = (await packetManager.SendPacket(receiver, MessageType.TalkReq, false, protocol, message));
            if (sentMessage == null)
            {
                // Well.... got another idea?
                await Task.Delay(100, token);
            }
        } while (sentMessage == null);

        // Yea... it does not return the original message, so we have to decode it to get the request id.
        // Either this or we have some more hacks.
        var talkReqMessage = (TalkReqMessage)messageDecoder.DecodeMessage(sentMessage);
        if (_logger.IsDebug) _logger.Debug($"Sent TalkReq to {destIpKey}, {nodeId} with request id {talkReqMessage.RequestId.ToHexString()}");
        return talkReqMessage;
    }

    public async Task<byte[]> CallAndWaitForResponse(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(HardCallTimeout);

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
}
