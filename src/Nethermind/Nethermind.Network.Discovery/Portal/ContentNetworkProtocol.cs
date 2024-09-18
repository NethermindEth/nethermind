// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

public class ContentNetworkProtocol(
    ContentNetworkConfig config,
    ITalkReqTransport talkReqTransport,
    ILogManager logManager
): IContentNetworkProtocol
{
    private readonly ILogger _logger = logManager.GetClassLogger<KademliaContentNetworkMessageSender>();
    private readonly byte[] _protocol = config.ProtocolId;

    public async Task<Pong> Ping(IEnr receiver, Ping ping, CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Sending Ping to {receiver}");

        byte[] pingBytes =
            SlowSSZ.Serialize(new MessageUnion()
            {
                Ping = ping
            });

        byte[] responseBytes = await talkReqTransport.CallAndWaitForResponse(receiver, _protocol, pingBytes, token);

        MessageUnion response = SlowSSZ.Deserialize<MessageUnion>(responseBytes);

        return response.Pong!;
    }

    public async Task<Nodes> FindNodes(IEnr receiver, FindNodes findNodes, CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Sending FindNodes to {receiver}");

        byte[] findNodesBytes = SlowSSZ.Serialize(new MessageUnion()
        {
            FindNodes = findNodes
        });

        byte[] response = await talkReqTransport.CallAndWaitForResponse(receiver, _protocol, findNodesBytes, token);
        Nodes nodes = SlowSSZ.Deserialize<MessageUnion>(response).Nodes!;

        if (_logger.IsDebug) _logger.Debug($"Received {nodes.Enrs.Length} from {receiver}");
        return nodes;
    }

    public async Task<Content> FindContent(IEnr receiver, FindContent findContent, CancellationToken token)
    {
        if (_logger.IsDebug) _logger.Debug($"Sending FindContent to {receiver}");

        byte[] findContentBytes = SlowSSZ.Serialize(new MessageUnion()
        {
            FindContent = findContent
        });

        byte[] response = await talkReqTransport.CallAndWaitForResponse(receiver, _protocol, findContentBytes, token);
        MessageUnion union;
        Content message;
        try
        {
            union = SlowSSZ.Deserialize<MessageUnion>(response);
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

        var message = SlowSSZ.Serialize(new MessageUnion()
        {
            Offer = offer
        });

        var responseByte = await talkReqTransport.CallAndWaitForResponse(receiver, _protocol, message, token);
        return SlowSSZ.Deserialize<MessageUnion>(responseByte).Accept!;
    }
}
