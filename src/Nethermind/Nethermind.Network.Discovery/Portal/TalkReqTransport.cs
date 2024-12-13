// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Translate whatever is in Lantern into an ITalkReqTransport.
/// </summary>
public class TalkReqTransport : ITalkReqTransport
{
    private readonly ILogger _logger;

    /// <summary>
    /// Hard timeout for each call and wait for response.
    /// </summary>
    private readonly TimeSpan _hardCallTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<byte[], TaskCompletionSource<TalkRespMessage>>.AlternateLookup<ReadOnlySpan<byte>> _requestResp;
    private readonly Dictionary<byte[], ITalkReqProtocolHandler>.AlternateLookup<ReadOnlySpan<byte>> _protocolHandlers;
    private readonly IRawTalkReqSender rawTalkReqSender;

    /// <param name="logManager"></param>
    public TalkReqTransport(
        IRawTalkReqSender _rawTalkReqSender,
        ILogManager logManager
)
    {
        rawTalkReqSender = _rawTalkReqSender;
        _logger = logManager.GetClassLogger<TalkReqTransport>();

        _requestResp = new ConcurrentDictionary<byte[], TaskCompletionSource<TalkRespMessage>>(Bytes.EqualityComparer).GetAlternateLookup<ReadOnlySpan<byte>>();
        _protocolHandlers = new Dictionary<byte[], ITalkReqProtocolHandler>(Bytes.EqualityComparer).GetAlternateLookup<ReadOnlySpan<byte>>();
    }

    public async Task<byte[]?> OnTalkReq(IEnr sender, TalkReqMessage message)
    {
        if (!_protocolHandlers.TryGetValue(message.Protocol, out var handler))
        {
            if (_logger.IsDebug) _logger.Debug($"Unknown msg req for protocol {message.Protocol.ToHexString()}");
            return null;
        }

        try
        {
            return await handler.OnMsgReq(sender, message);
        }
        catch (Exception e)
        {
            _logger.Error("Error handling talkreq.", e);
            throw;
        }
    }

    public void OnTalkResp(IEnr sender, TalkRespMessage message)
    {
        if (_requestResp.TryRemove(message.RequestId, out TaskCompletionSource<TalkRespMessage>? resp))
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

    public Task<TalkReqMessage> SendTalkReq(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token)
    {
        return rawTalkReqSender.SentTalkReq(receiver, protocol, message, token);
    }

    public async Task<byte[]> CallAndWaitForResponse(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(_hardCallTimeout);

        TalkReqMessage talkReqMessage = await SendTalkReq(receiver, protocol, message, token);

        try
        {
            var talkReq = new TaskCompletionSource<TalkRespMessage>(cts.Token);
            if (!_requestResp.TryAdd(talkReqMessage.RequestId, talkReq))
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
                _logger.Debug($"TalkResp to {destIpKey?.Value} with id {talkReqMessage.RequestId} timed out");
            }
            throw;
        }
        finally
        {
            _requestResp.TryRemove(talkReqMessage.RequestId, out _);
        }
    }
}
