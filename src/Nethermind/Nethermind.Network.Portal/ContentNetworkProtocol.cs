// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Portal.Messages;
using Nethermind.Serialization;

namespace Nethermind.Network.Portal;

public class ContentNetworkProtocol(
    ContentNetworkConfig config,
    ITalkReqTransport talkReqTransport,
    ILogManager logManager
) : IContentNetworkProtocol
{
    private readonly ILogger _logger = logManager.GetClassLogger<KademliaContentNetworkContentKademliaMessageSender>();
    private readonly byte[] _protocol = config.ProtocolId;

    public async Task<Pong> Ping(IEnr receiver, Ping ping, CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Sending Ping to {receiver}");

        // Yes, I'm breaking some rules here a bit...
        ping.CustomPayload = config.ContentRadius.ToLittleEndian();

        var pingBytes = SszEncoding.Encode(new MessageUnion()
        {
            Selector = MessageType.Ping,
            Ping = ping,
        }).ToArray();

        var responseBytes = await talkReqTransport.CallAndWaitForResponse(receiver, _protocol, pingBytes, token);

        SszEncoding.Decode(responseBytes, out MessageUnion response);

        if (_logger.IsDebug) _logger.Debug($"Getting Pong from {receiver}");

        return response.Pong!;
    }

    public async Task<Nodes> FindNodes(IEnr receiver, FindNodes findNodes, CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Sending FindNodes to {receiver}");

        var findNodesBytes = SszEncoding.Encode(new MessageUnion()
        {
            Selector = MessageType.FindNodes,
            FindNodes = findNodes
        });

        var response = await talkReqTransport.CallAndWaitForResponse(receiver, _protocol, findNodesBytes, token);

        SszEncoding.Decode(response, out MessageUnion messageUnion);
        Nodes nodes = messageUnion.Nodes!;

        if (_logger.IsDebug) _logger.Debug($"Received {nodes.Enrs.Length} from {receiver}");
        return nodes;
    }

    public async Task<Content> FindContent(IEnr receiver, FindContent findContent, CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Sending FindContent to {receiver}");

        var findContentBytes = SszEncoding.Encode(new MessageUnion()
        {
            Selector = MessageType.FindContent,
            FindContent = findContent
        });

        var response = await talkReqTransport.CallAndWaitForResponse(receiver, _protocol, findContentBytes, token);
        MessageUnion union;
        Content message;
        try
        {
            SszEncoding.Decode(response, out union);
            message = union.Content!;
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Error deserialize {response.ToHexString()}. Request is {findContentBytes.ToHexString()}", e);
            throw;
        }

        return message;
    }

    public async Task<Accept> Offer(IEnr receiver, Offer offer, CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Sending Offer to {receiver}");

        var message = SszEncoding.Encode(new MessageUnion()
        {
            Selector = MessageType.Offer,
            Offer = offer
        });

        var responseByte = await talkReqTransport.CallAndWaitForResponse(receiver, _protocol, message, token);
        SszEncoding.Decode(responseByte, out MessageUnion union);
        return union.Accept!;
    }
}
