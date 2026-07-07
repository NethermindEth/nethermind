// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Autofac.Features.AttributeFilters;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public sealed class NeighborsMsgSerializer(
    IEcdsa ecdsa,
    [KeyFilter(IProtectedPrivateKey.NodeKey)]
    IPrivateKeyGenerator nodeKey,
    INodeIdResolver nodeIdResolver)
    : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IZeroInnerMessageSerializer<NeighborsMsg>
{
    private static readonly RlpLimit NodesRlpLimit = RlpLimit.For<NeighborsMsg>(16, nameof(NeighborsMsg.Nodes));

    private static Node DecodeNode(ref RlpReader ctx)
    {
        int lastPosition = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(lastPosition);
        ReadOnlySpan<byte> ip = ctx.DecodeByteArraySpan(IpAddressRlpLimit);
        IPEndPoint discoveryAddress = GetAddress(ip, ctx.DecodeInt());
        IPEndPoint address = discoveryAddress;
        if (count > 3)
        {
            address = GetAddress(ip, ctx.DecodeInt());
        }

        ReadOnlySpan<byte> id = ctx.DecodeByteArraySpan(NodeIdRlpLimit);
        return new Node(new PublicKey(id), address, discoveryAddress.Port);
    }

    public void Serialize(IByteBuffer byteBuffer, NeighborsMsg msg)
    {
        (int totalLength, int contentLength, int nodesContentLength) = GetLength(msg);

        byteBuffer.MarkIndex();
        PrepareBufferForSerialization(byteBuffer, totalLength, (byte)msg.MsgType);
        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(contentLength);
        if (msg.Nodes.Count != 0)
        {
            writer.StartSequence(nodesContentLength);
            for (int i = 0; i < msg.Nodes.Count; i++)
            {
                Node node = msg.Nodes[i];
                SerializeNode(ref writer, node);
            }
        }
        else
        {
            writer.Encode(Rlp.OfEmptyList);
        }

        writer.Encode(msg.ExpirationTime);
        byteBuffer.ResetIndex();

        AddSignatureAndMdc(byteBuffer, totalLength + 1);
    }

    public NeighborsMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey FarPublicKey, _, IByteBuffer Data) = PrepareForDeserialization(msgBytes);

        RlpReader ctx = new(Data.AsSpan());
        ctx.ReadSequenceLength();
        int nodesEnd = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(nodesEnd);
        ctx.GuardLimit(count, NodesRlpLimit);
        Node[] decoded = new Node[count];
        int nodeCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (ctx.IsNextItemEmptyList())
            {
                ctx.SkipItem();
                continue;
            }

            decoded[nodeCount++] = DecodeNode(ref ctx);
        }

        ctx.Check(nodesEnd);
        if (nodeCount != decoded.Length)
        {
            Array.Resize(ref decoded, nodeCount);
        }

        long expirationTime = ctx.DecodeLong();
        Data.SetReaderIndex(Data.ReaderIndex + ctx.Position);
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

    public int GetLength(NeighborsMsg msg, out int contentLength)
    {
        (int totalLength, contentLength, int _) = GetLength(msg);
        return totalLength;
    }

    private static (int totalLength, int contentLength, int nodesContentLength) GetLength(NeighborsMsg msg)
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
