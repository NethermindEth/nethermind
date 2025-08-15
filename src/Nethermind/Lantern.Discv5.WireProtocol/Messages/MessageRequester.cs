using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Messages;

public class MessageRequester(IIdentityManager identityManager, IRequestManager requestManager,
        ILoggerFactory loggerFactory)
    : IMessageRequester
{
    private readonly ILogger<MessageRequester> _logger = LoggerFactoryExtensions.CreateLogger<MessageRequester>(loggerFactory);

    private static void ReportRequestAddedFailure(ILogger<MessageRequester> _logger, bool isCached, MessageType messageType, byte[] requestId, byte[] destNodeId)
    {
        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Failed to add {Type} {MessageType} request. Id: {RequestId}, dst: {DestNodeId}",
                                                                    isCached ? "cached" : "pending",
                                                                    messageType,
                                                                    Convert.ToHexString(requestId),
                                                                    Convert.ToHexString(destNodeId));
    }

    public byte[]? ConstructPingMessage(byte[] destNodeId)
    {
        _logger.LogTrace("Constructing message of type {MessageType}", MessageType.Ping);
        var pingMessage = new PingMessage((int)identityManager.Record.SequenceNumber);
        var pendingRequest = new PendingRequest(destNodeId, pingMessage);
        var result = requestManager.AddPendingRequest(pingMessage.RequestId, pendingRequest);

        if (!result)
        {
            ReportRequestAddedFailure(_logger, false, MessageType.Ping, pingMessage.RequestId, destNodeId);
            return null;
        }

        _logger.LogTrace("Ping message constructed: {PingMessage}", pingMessage.RequestId);
        return pingMessage.EncodeMessage();
    }

    public byte[]? ConstructCachedPingMessage(byte[] destNodeId)
    {
        _logger.LogTrace("Constructing message of type {MessageType}", MessageType.Ping);
        var pingMessage = new PingMessage((int)identityManager.Record.SequenceNumber);
        var cachedRequest = new CachedRequest(destNodeId, pingMessage);
        var result = requestManager.AddCachedRequest(destNodeId, cachedRequest);

        if (!result)
        {
            ReportRequestAddedFailure(_logger, true, MessageType.Ping, pingMessage.RequestId, destNodeId);
            return null;
        }

        _logger.LogTrace("Ping message constructed: {PingMessage}", pingMessage.RequestId);
        return pingMessage.EncodeMessage();
    }

    public byte[]? ConstructFindNodeMessage(byte[] destNodeId, bool isLookupRequest, int[] distances)
    {
        _logger.LogTrace("Constructing message of type {MessageType} at distances {Distances}", MessageType.FindNode, string.Join(", ", distances.Select(d => d.ToString())));

        var findNodesMessage = new FindNodeMessage(distances);
        var pendingRequest = new PendingRequest(destNodeId, findNodesMessage)
        {
            IsLookupRequest = isLookupRequest
        };
        var result = requestManager.AddPendingRequest(findNodesMessage.RequestId, pendingRequest);

        if (!result)
        {
            ReportRequestAddedFailure(_logger, false, MessageType.FindNode, findNodesMessage.RequestId, destNodeId);
            return null;
        }

        _logger.LogTrace("FindNode message constructed: {FindNodeMessage}", findNodesMessage.RequestId);
        return findNodesMessage.EncodeMessage();
    }

    public byte[]? ConstructCachedFindNodeMessage(byte[] destNodeId, bool isLookupRequest, int[] distances)
    {
        _logger.LogTrace("Constructing message of type {MessageType} at distances {Distances}", MessageType.FindNode, string.Join(", ", distances.Select(d => d.ToString())));

        var findNodesMessage = new FindNodeMessage(distances);
        var cachedRequest = new CachedRequest(destNodeId, findNodesMessage)
        {
            IsLookupRequest = isLookupRequest
        };
        var result = requestManager.AddCachedRequest(destNodeId, cachedRequest);

        if (!result)
        {
            ReportRequestAddedFailure(_logger, true, MessageType.FindNode, findNodesMessage.RequestId, destNodeId);
            return null;
        }

        _logger.LogTrace("FindNode message constructed: {FindNodeMessage}", findNodesMessage.RequestId);
        return findNodesMessage.EncodeMessage();
    }

    public byte[]? ConstructTalkReqMessage(byte[] destNodeId, byte[] protocol, byte[] request)
    {
        _logger.LogTrace("Constructing message of type {MessageType}", MessageType.TalkReq);

        var talkReqMessage = new TalkReqMessage(protocol, request);
        var pendingRequest = new PendingRequest(destNodeId, talkReqMessage);
        var result = requestManager.AddPendingRequest(talkReqMessage.RequestId, pendingRequest);

        if (!result)
        {
            ReportRequestAddedFailure(_logger, false, MessageType.TalkReq, talkReqMessage.RequestId, destNodeId);
            return null;
        }

        _logger.LogTrace("TalkReq message constructed: {TalkReqMessage}", talkReqMessage.RequestId);
        return talkReqMessage.EncodeMessage();
    }

    public byte[]? ConstructTalkRespMessage(byte[] destNodeId, byte[] response)
    {
        _logger.LogTrace("Constructing message of type {MessageType}", MessageType.TalkResp);

        var talkRespMessage = new TalkRespMessage(response);
        var pendingRequest = new PendingRequest(destNodeId, talkRespMessage);
        var result = requestManager.AddPendingRequest(talkRespMessage.RequestId, pendingRequest);

        if (!result)
        {
            ReportRequestAddedFailure(_logger, false, MessageType.TalkResp, talkRespMessage.RequestId, destNodeId);
            return null;
        }

        _logger.LogTrace("TalkResp message constructed: {TalkRespMessage}", talkRespMessage.RequestId);
        return talkRespMessage.EncodeMessage();
    }

    public byte[]? ConstructCachedTalkReqMessage(byte[] destNodeId, byte[] protocol, byte[] request)
    {
        _logger.LogTrace("Constructing message of type {MessageType}", MessageType.TalkReq);

        var talkReqMessage = new TalkReqMessage(protocol, request);
        var cachedRequest = new CachedRequest(destNodeId, talkReqMessage);
        var result = requestManager.AddCachedRequest(destNodeId, cachedRequest);

        if (!result)
        {
            ReportRequestAddedFailure(_logger, true, MessageType.TalkReq, talkReqMessage.RequestId, destNodeId);
            return null;
        }

        _logger.LogTrace("TalkReq message constructed: {TalkReqMessage}", talkReqMessage.RequestId);
        return talkReqMessage.EncodeMessage();
    }

    public byte[]? ConstructCachedTalkRespMessage(byte[] destNodeId, byte[] response)
    {
        _logger.LogTrace("Constructing message of type {MessageType}", MessageType.TalkResp);

        var talkRespMessage = new TalkRespMessage(response);
        var cachedRequest = new CachedRequest(destNodeId, talkRespMessage);
        var result = requestManager.AddCachedRequest(destNodeId, cachedRequest);

        if (!result)
        {
            ReportRequestAddedFailure(_logger, true, MessageType.FindNode, talkRespMessage.RequestId, destNodeId);
            return null;
        }

        _logger.LogTrace("TalkResp message constructed: {TalkRespMessage}", talkRespMessage.RequestId);
        return talkRespMessage.EncodeMessage();
    }
}
