// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Kademlia.Content;
using Nethermind.Network.Discovery.Portal.Messages;
using Nethermind.Serialization;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Adapter from IContentNetworkProtocol to Kademlia's IMessageSender, which is its outgoing transport.
/// </summary>
/// <param name="config"></param>
/// <param name="contentNetworkProtocol"></param>
/// <param name="enrProvider"></param>
/// <param name="logManager"></param>
public class KademliaContentNetworkContentKademliaMessageSender(
    ContentNetworkConfig config,
    RadiusTracker radiusTracker,
    IContentNetworkProtocol contentNetworkProtocol,
    IEnrProvider enrProvider,
    ILogManager logManager
) : IContentMessageSender<IEnr, byte[], LookupContentResult>, IKademliaMessageSender<IEnr>
{
    private readonly EnrNodeHashProvider _nodeHashProvider = EnrNodeHashProvider.Instance;
    private readonly ILogger _logger = logManager.GetClassLogger<KademliaContentNetworkContentKademliaMessageSender>();

    public async Task Ping(IEnr receiver, CancellationToken token)
    {
        Pong pong = await contentNetworkProtocol.Ping(receiver, new Ping()
        {
            EnrSeq = enrProvider.SelfEnr.SequenceNumber,
            // Note: This custom payload of type content radius is actually history network specific
            CustomPayload = config.ContentRadius.ToLittleEndian()
        }, token);

        if (pong.CustomPayload.Length == 32)
        {
            UInt256 radius = new UInt256(pong.CustomPayload, false);
            radiusTracker.UpdatePeerRadius(receiver, radius);
        }
    }

    public async Task<IEnr[]> FindNeighbours(IEnr receiver, ValueHash256 hash, CancellationToken token)
    {
        // For some reason, the protocol uses the distance instead of the standard kademlia RPC
        // which checks for the k-nearest nodes and just let the implementation decide how to implement it.
        // With the most basic implementation, this is the same as returning the bucket of the distance between
        // the target and current node. But more sophisticated routing table can do more if just query with
        // nodeid.
        ushort theDistance = (ushort)Hash256XorUtils.CalculateDistance(_nodeHashProvider.GetHash(receiver), hash);

        // To simulate a neighbour query to a particular hash with distance, we also query for neighbouring
        // bucket in the order as if we are running a query to a particular hash
        int toAddExtra = 4;
        ushort[] queryDistance = new ushort[1 + toAddExtra];
        bool nowUpper = true;
        ushort upper = theDistance;
        ushort lower = theDistance;
        queryDistance[0] = theDistance;
        for (int i = 0; i < toAddExtra; i++)
        {
            if (nowUpper && upper < 255)
            {
                upper++;
                queryDistance[i + 1] = upper;
                nowUpper = false;
            }
            else if (lower > 0)
            {
                lower--;
                queryDistance[i + 1] = lower;
                nowUpper = true;
            }
        }
        Array.Sort(queryDistance);

        Nodes message = await contentNetworkProtocol.FindNodes(receiver, new FindNodes()
        {
            Distances = queryDistance
        }, token);

        return message.Enrs.Select(enr => enrProvider.Decode(enr.Data)).ToArray();
    }

    public async Task<FindValueResponse<IEnr, LookupContentResult>> FindValue(IEnr receiver, byte[] contentKey, CancellationToken token)
    {
        Content message = await contentNetworkProtocol.FindContent(receiver, new FindContent()
        {
            ContentKey = new ContentKey { Data = contentKey }
        }, token);

        if (message.ConnectionId == default && message.Payload == null)
        {
            IEnr[] enrs = message.Enrs!.Select(enr => enrProvider.Decode(enr.Data)).ToArray();
            return new FindValueResponse<IEnr, LookupContentResult>(false, null, enrs);
        }

        if (_logger.IsInfo) _logger.Info($"Received value from {receiver.NodeId.ToHexString()} {message.ConnectionId}");

        return new FindValueResponse<IEnr, LookupContentResult>(true, new LookupContentResult()
        {
            ConnectionId = message.ConnectionId,
            Payload = message.Payload,
            NodeId = receiver,
        }, Array.Empty<IEnr>());
    }
}
