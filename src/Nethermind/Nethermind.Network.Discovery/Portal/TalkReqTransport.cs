// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using NonBlocking;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Translate whatever is in Lantern into an ITalkReqTransport.
/// </summary>
/// <param name="logManager"></param>
public class TalkReqTransport(
    IRawTalkReqSender rawTalkReqSender,
    ILogManager logManager
): ITalkReqTransport
{
    private ILogger _logger = logManager.GetClassLogger<TalkReqTransport>();
    private readonly TimeSpan HardCallTimeout = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<TalkRespMessage>> _requestResp = new();
    private readonly SpanDictionary<byte, ITalkReqProtocolHandler> _protocolHandlers = new(Bytes.SpanEqualityComparer);

    public async Task<byte[]?> OnTalkReq(IEnr sender, TalkReqMessage message)
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

    public Task<TalkReqMessage> SentTalkReq(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token)
    {
        return rawTalkReqSender.SentTalkReq(receiver, protocol, message, token);
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
            if (_logger.IsTrace) _logger.Trace($"Received TalkResp from {receiver}");
            return response.Response;
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsDebug)
            {
                var destIpKey = receiver.GetEntry<EntryIp>(EnrEntryKey.Ip);
                _logger.Debug($"TalkResp to {destIpKey?.Value} with id {requestId} timed out");
            }
            throw;
        }
        finally
        {
            _requestResp.TryRemove(requestId, out _);
        }
    }
}
