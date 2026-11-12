// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public sealed class NeighborsMsgSerializer(
    IEcdsa ecdsa,
    [KeyFilter(IProtectedPrivateKey.NodeKey)]
    IPrivateKeyGenerator nodeKey,
    INodeIdResolver nodeIdResolver)
    : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IMessageSerializer<NeighborsMsg>
{
    private static readonly RlpLimit NodesRlpLimit = RlpLimit.For<NeighborsMsg>(16, nameof(NeighborsMsg.Nodes));

    private static Node DecodeNode(ref RlpReader reader)
    {
        RlpReader ctx = reader.EnterSequence(out int itemLength);
        int count = ctx.CountItems();
        ReadOnlySpan<byte> ip = ctx.DecodeByteArraySpan(IpAddressRlpLimit);
        IPEndPoint discoveryAddress = GetAddress(ip, ctx.DecodeInt());
        IPEndPoint address = discoveryAddress;
        if (count > 3)
        {
            int tcpPort = ctx.DecodeInt();
            address = tcpPort == 0
                ? new IPEndPoint(discoveryAddress.Address, 0)
                : GetAddress(ip, tcpPort);
        }

        ReadOnlySpan<byte> id = ctx.DecodeByteArraySpan(NodeIdRlpLimit);
        reader.ExitSequenceIgnoringRest(ctx, itemLength);
        return new Node(new PublicKey(id), address, discoveryAddress.Port);
    }

    public int Serialize(Span<byte> destination, NeighborsMsg msg)
    {
        (_, int contentLength, int nodesContentLength) = GetDataLength(msg);

        RlpWriter writer = StartPacket(destination, (byte)msg.MsgType);
        RlpSequence sequence = writer.StartSequence(contentLength);
        if (msg.Nodes.Count != 0)
        {
            RlpSequence nodes = writer.StartSequence(nodesContentLength);
            for (int i = 0; i < msg.Nodes.Count; i++)
            {
                Node node = msg.Nodes[i];
                SerializeNode(ref writer, node);
            }

            writer.EndSequence(nodes);
        }
        else
        {
            writer.Encode(Rlp.OfEmptyList);
        }

        writer.Encode(msg.ExpirationTime);
        writer.EndSequence(sequence);

        return CompletePacket(destination, in writer, out _);
    }

    public NeighborsMsg Deserialize(ReadOnlySpan<byte> msgBytes)
    {
        (PublicKey FarPublicKey, _) = PrepareForDeserialization(msgBytes, out ReadOnlySpan<byte> data);

        RlpReader ctx = new RlpReader(data).EnterSequence(out _);
        RlpReader nodes = ctx.EnterSequence(out int nodesLength);
        int count = nodes.CountItems(NodesRlpLimit);
        Node[] decoded = new Node[count];
        int nodeCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (nodes.TryReadEmptySequence())
            {
                continue;
            }

            decoded[nodeCount++] = DecodeNode(ref nodes);
        }

        ctx.ExitSequence(nodes, nodesLength);
        if (nodeCount != decoded.Length)
        {
            Array.Resize(ref decoded, nodeCount);
        }

        long expirationTime = ctx.DecodeLong();
        NeighborsMsg msg = new(FarPublicKey, expirationTime, decoded);
        return msg;
    }

    private static int GetNodesLength(ArraySegment<Node> nodes, out int contentLength)
    {
        contentLength = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            Node node = nodes[i];
            contentLength += Rlp.LengthOfSequence(GetLengthSerializeNode(node));
        }
        return Rlp.LengthOfSequence(contentLength);
    }

    public int GetLength(NeighborsMsg msg) => GetPacketLength(GetDataLength(msg).totalLength);

    private static (int totalLength, int contentLength, int nodesContentLength) GetDataLength(NeighborsMsg msg)
    {
        int nodesContentLength = 0;
        int contentLength = 0;
        if (msg.Nodes.Count != 0)
        {
            contentLength += GetNodesLength(msg.Nodes, out nodesContentLength);
        }
        else
        {
            contentLength += Rlp.OfEmptyList.Bytes.Length;
        }

        contentLength += Rlp.LengthOf(msg.ExpirationTime);

        return (Rlp.LengthOfSequence(contentLength), contentLength, nodesContentLength);
    }
}
