// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

public class KademliaTalkReqHandler(
    IKademlia<IEnr, byte[], LookupContentResult> kad,
    IEnr selfEnr,
    IUtpManager utpManager
) : ITalkReqProtocolHandler
{
    private readonly EnrNodeHashProvider _nodeHashProvider = new EnrNodeHashProvider();
    public async Task<byte[]?> OnMsgReq(IEnr sender, TalkReqMessage talkReqMessage)
    {
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
        ValueHash256 theHash = Hash256XORUtils.RandomizeHashAtDistance(_nodeHashProvider.GetHash(selfEnr), nodes.Distances[0]);
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
            if (value.Payload!.Length>  utpManager.MaxContentByteSize)
            {
                value.ConnectionId = utpManager.InitiateUtpStreamSender(sender, value.Payload);
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
}
