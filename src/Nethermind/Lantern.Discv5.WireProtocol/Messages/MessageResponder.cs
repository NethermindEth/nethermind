using System.Net;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Messages;

public class MessageResponder(IIdentityManager identityManager,
        IRoutingTable routingTable,
        IPacketReceiver packetReceiver,
        IRequestManager requestManager,
        ILookupManager lookupManager,
        IMessageDecoder messageDecoder,
        ILoggerFactory loggerFactory,
        ITalkReqAndRespHandler? talkResponder = null)
    : IMessageResponder
{
    private const int RecordLimit = 16;
    private const int MaxRecordsPerMessage = 3;
    private readonly ILogger<MessageResponder> _logger = LoggerFactoryExtensions.CreateLogger<MessageResponder>(loggerFactory);

    public async Task<byte[][]?> HandleMessageAsync(byte[] message, IPEndPoint endPoint)
    {
        var messageType = (MessageType)message[0];

        return messageType switch
        {
            MessageType.Ping => HandlePingMessage(message, endPoint),
            MessageType.Pong => HandlePongMessage(message),
            MessageType.FindNode => HandleFindNodeMessage(message),
            MessageType.Nodes => await HandleNodesMessageAsync(message),
            MessageType.TalkReq => HandleTalkReqMessage(message),
            MessageType.TalkResp => HandleTalkRespMessage(message),
            _ => null
        };
    }

    private byte[][] HandlePingMessage(byte[] message, IPEndPoint endPoint)
    {
        _logger.LogTrace("Received message type => {MessageType}", MessageType.Ping);
        var decodedMessage = messageDecoder.DecodeMessage(message);
        var localEnrSeq = identityManager.Record.SequenceNumber;
        var pongMessage = new PongMessage(decodedMessage.RequestId, (int)localEnrSeq, endPoint.Address, endPoint.Port);
        var responseMessage = new List<byte[]> { pongMessage.EncodeMessage() };

        return responseMessage.ToArray();
    }

    private byte[][]? HandlePongMessage(byte[] message)
    {
        _logger.LogTrace("Received message type => {MessageType}", MessageType.Pong);

        var decodedMessage = (PongMessage)messageDecoder.DecodeMessage(message);
        var pendingRequest = GetPendingRequest(decodedMessage);

        if (pendingRequest == null)
        {
            _logger.LogDebug("Received PONG message with no pending request. Ignoring message");
            return null;
        }

        var nodeEntry = routingTable.GetNodeEntryForNodeId(pendingRequest.NodeId);

        if (nodeEntry == null)
        {
            _logger.LogDebug("ENR record is not known. Cannot handle PONG message from node. Node ID: {NodeId}", Convert.ToHexString(pendingRequest.NodeId));
            return null;
        }

        packetReceiver.RaisePongResponseReceived(new PongResponseEventArgs(decodedMessage.RequestId, decodedMessage));

        if (nodeEntry.Status != NodeStatus.Live)
        {
            routingTable.UpdateFromEnr(nodeEntry.Record);
            routingTable.MarkNodeAsLive(nodeEntry.Id);
            routingTable.MarkNodeAsResponded(pendingRequest.NodeId);

            if (identityManager.IsIpAddressAndPortSet())
                return null;

            var endpoint = new IPEndPoint(decodedMessage.RecipientIp, decodedMessage.RecipientPort);
            identityManager.UpdateIpAddressAndPort(endpoint);

            return null;
        }

        if (decodedMessage.EnrSeq <= (int)nodeEntry.Record.SequenceNumber)
        {
            return null;
        }

        var distance = new[] { 0 };
        var findNodesMessage = new FindNodeMessage(distance);
        var result = requestManager.AddPendingRequest(findNodesMessage.RequestId, new PendingRequest(pendingRequest.NodeId, findNodesMessage));

        if (!result)
        {
            _logger.LogDebug("Failed to add pending request. Request id: {RequestId}", Convert.ToHexString(findNodesMessage.RequestId));
            return null;
        }

        var responseMessage = new List<byte[]> { findNodesMessage.EncodeMessage() };

        return responseMessage.ToArray();
    }

    private byte[][] HandleFindNodeMessage(byte[] message)
    {
        _logger.LogTrace("Received message type => {MessageType}", MessageType.FindNode);
        var decodedMessage = (FindNodeMessage)messageDecoder.DecodeMessage(message);
        var closestNodes = routingTable.GetEnrRecordsAtDistances(decodedMessage.Distances)!.Take(RecordLimit).ToArray();
        var chunkedRecords = SplitIntoChunks(closestNodes, MaxRecordsPerMessage);
        var responses = chunkedRecords.Select(chunk => new NodesMessage(decodedMessage.RequestId, chunk.Length, chunk)).Select(nodesMessage => nodesMessage.EncodeMessage()).ToArray();

        if (responses.Length == 0)
        {
            var response = new NodesMessage(decodedMessage.RequestId, closestNodes.Length, Array.Empty<Enr.Enr>())
                .EncodeMessage();
            return new List<byte[]> { response }.ToArray();
        }

        _logger.LogTrace("Sending a total of {EnrRecords} with {Responses} responses", closestNodes.Length, responses.Length);

        return responses;
    }

    private async Task<byte[][]?> HandleNodesMessageAsync(byte[] message)
    {
        _logger.LogInformation("Received message type => {MessageType}", MessageType.Nodes);
        var decodedMessage = (NodesMessage)messageDecoder.DecodeMessage(message);
        var pendingRequest = GetPendingRequest(decodedMessage);

        if (pendingRequest == null)
        {
            _logger.LogDebug("Received NODES message with no pending request. Ignoring message");
            return null;
        }

        pendingRequest.MaxResponses = decodedMessage.Total;

        if (pendingRequest.ResponsesCount > decodedMessage.Total)
        {
            _logger.LogDebug("Expected {ExpectedResponsesCount} from node {NodeId} but received {TotalResponsesCount}. Ignoring response", decodedMessage.Total, Convert.ToHexString(pendingRequest.NodeId), pendingRequest.ResponsesCount);
            return null;
        }

        var findNodesRequest = (FindNodeMessage)messageDecoder.DecodeMessage(pendingRequest.Message.EncodeMessage());
        var receivedNodes = new List<NodeTableEntry>();

        try
        {
            foreach (var distance in findNodesRequest.Distances)
            {
                foreach (var enr in decodedMessage.Enrs)
                {
                    var nodeId = identityManager.Verifier.GetNodeIdFromRecord(enr);
                    var distanceToNode = TableUtility.Log2Distance(nodeId, pendingRequest.NodeId);

                    if (distance != distanceToNode)
                        continue;

                    if (pendingRequest.IsLookupRequest)
                    {
                        if (routingTable.GetNodeEntryForNodeId(nodeId) == null)
                        {
                            routingTable.UpdateFromEnr(enr);
                        }

                        var nodeEntry = routingTable.GetNodeEntryForNodeId(nodeId);
                        if (nodeEntry != null)
                        {
                            receivedNodes.Add(nodeEntry);
                        }
                    }
                    else
                    {
                        receivedNodes.Add(new NodeTableEntry(enr, new IdentityVerifierV4()));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling NODES message");
            return null;
        }

        await lookupManager.ContinueLookupAsync(receivedNodes, pendingRequest.NodeId, decodedMessage.Total);
        packetReceiver.RaiseNodesResponseReceived(new NodesResponseEventArgs(decodedMessage.RequestId, receivedNodes, pendingRequest.ResponsesCount == decodedMessage.Total));

        return null;
    }

    private byte[][]? HandleTalkReqMessage(byte[] message)
    {
        if (talkResponder == null)
        {
            _logger.LogWarning("Talk responder is not set. Cannot handle talk request message");
            return null;
        }

        _logger.LogTrace("Received message type => {MessageType}", MessageType.TalkReq);
        var decodedMessage = (TalkReqMessage)messageDecoder.DecodeMessage(message);
        var responses = talkResponder.HandleRequest(decodedMessage.Protocol, decodedMessage.Request);

        if (responses == null)
        {
            return null;
        }

        var responseMessages = new List<byte[]>();

        foreach (var response in responses)
        {
            var talkRespMessage = new TalkRespMessage(decodedMessage.RequestId, response);
            responseMessages.Add(talkRespMessage.EncodeMessage());
        }

        return responseMessages.ToArray();
    }

    private byte[][]? HandleTalkRespMessage(byte[] message)
    {
        if (talkResponder == null)
        {
            _logger.LogWarning("Talk responder is not set. Cannot handle talk response message");
            return null;
        }

        _logger.LogTrace("Received message type => {MessageType}", MessageType.TalkResp);

        var decodedMessage = (TalkRespMessage)messageDecoder.DecodeMessage(message);
        var pendingRequest = GetPendingRequest(decodedMessage);

        if (pendingRequest == null)
        {
            _logger.LogDebug("Received TALKRESP message with no pending request. Ignoring message");
            return null;
        }

        talkResponder.HandleResponse(decodedMessage.Response);

        return null;
    }

    private PendingRequest? GetPendingRequest(Message message)
    {
        var pendingRequest = requestManager.MarkRequestAsFulfilled(message.RequestId);

        if (pendingRequest == null)
        {
            _logger.LogTrace("Received message with unknown request id. Message Type: {MessageType}, Request id: {RequestId}", message.MessageType, Convert.ToHexString(message.RequestId));
            return null;
        }

        routingTable.MarkNodeAsLive(pendingRequest.NodeId);
        routingTable.MarkNodeAsResponded(pendingRequest.NodeId);

        return requestManager.GetPendingRequest(message.RequestId);
    }

    private static IEnumerable<T[]> SplitIntoChunks<T>(IReadOnlyCollection<T> array, int chunkSize)
    {
        for (var i = 0; i < array.Count; i += chunkSize)
        {
            yield return array.Skip(i).Take(chunkSize).ToArray();
        }
    }
}
