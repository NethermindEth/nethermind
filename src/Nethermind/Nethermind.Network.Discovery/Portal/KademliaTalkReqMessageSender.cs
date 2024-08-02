// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Adapter from TalkReq/Resp to Kademlia's IMessageSender, which is its outgoing transport.
/// </summary>
/// <param name="protocol"></param>
/// <param name="talkReqTransport"></param>
/// <param name="enrProvider"></param>
/// <param name="logManager"></param>
public class KademliaTalkReqMessageSender(
    byte[] protocol,
    ITalkReqTransport talkReqTransport,
    IEnrProvider enrProvider,
    ILogManager logManager
) : IMessageSender<IEnr, byte[], LookupContentResult>
{
    private readonly EnrNodeHashProvider _nodeHashProvider = EnrNodeHashProvider.Instance;
    private ILogger _logger = logManager.GetClassLogger<KademliaTalkReqMessageSender>();

    public async Task Ping(IEnr receiver, CancellationToken token)
    {
        // TODO: update distance
        byte[] pingBytes =
            SlowSSZ.Serialize(new MessageUnion()
            {
                Ping = new Ping()
                {
                    EnrSeq = enrProvider.SelfEnr.SequenceNumber,
                    CustomPayload = Array.Empty<byte>()
                }
            });

        await talkReqTransport.CallAndWaitForResponse(receiver, protocol, pingBytes, token);
    }

    public async Task<IEnr[]> FindNeighbours(IEnr receiver, ValueHash256 hash, CancellationToken token)
    {
        // For some reason, the protocol uses the distance instead of the standard kademlia RPC
        // which checks for the k-nearest nodes and just let the implementation decide how to implement it.
        // With the most basic implementation, this is the same as returning the bucket of the distance between
        // the target and current node. But more sophisticated routing table can do more if just query with
        // nodeid.
        ushort theDistance = (ushort)Hash256XORUtils.CalculateDistance(_nodeHashProvider.GetHash(receiver), hash);

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
                queryDistance[i+1] = upper;
                nowUpper = false;
            }
            else if (lower > 0)
            {
                lower--;
                queryDistance[i+1] = lower;
                nowUpper = true;
            }
        }

        byte[] pingBytes = SlowSSZ.Serialize(new MessageUnion()
        {
            FindNodes = new FindNodes()
            {
                Distances = queryDistance
            }
        });

        byte[] response = await talkReqTransport.CallAndWaitForResponse(receiver, protocol, pingBytes, token);

        Nodes message = SlowSSZ.Deserialize<MessageUnion>(response).Nodes!;

        IEnr[] enrs = new IEnr[message.Enrs.Length];

        for (var i = 0; i < message.Enrs.Length; i++)
        {
            enrs[i] = enrProvider.Decode(message.Enrs[i]);
        }

        return enrs;
    }

    public async Task<FindValueResponse<IEnr, LookupContentResult>> FindValue(IEnr receiver, byte[] contentKey, CancellationToken token)
    {
        byte[] findContentBytes = SlowSSZ.Serialize(new MessageUnion()
        {
            FindContent = new FindContent()
            {
                ContentKey = contentKey
            }
        });

        byte[] response = await talkReqTransport.CallAndWaitForResponse(receiver, protocol, findContentBytes, token);
        Content message;
        try
        {
            message = SlowSSZ.Deserialize<MessageUnion>(response).Content!;
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Error deserialize {response.ToHexString()}. Request is {findContentBytes.ToHexString()}", e);
            throw;
        }

        if (message.ConnectionId == null && message.Payload == null)
        {
            IEnr[] enrs = new IEnr[message.Enrs!.Length];
            for (var i = 0; i < message.Enrs.Length; i++)
            {
                enrs[i] = enrProvider.Decode(message.Enrs[i]);
            }

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
