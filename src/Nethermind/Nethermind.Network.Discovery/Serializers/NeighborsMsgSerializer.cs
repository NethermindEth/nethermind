// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Autofac.Features.AttributeFilters;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Serializers;

public class NeighborsMsgSerializer : DiscoveryMsgSerializerBase, IZeroInnerMessageSerializer<NeighborsMsg>
{
    private static readonly Func<RlpStream, Node> _decodeItem = static ctx =>
    {
        int lastPosition = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(lastPosition);

        ReadOnlySpan<byte> ip = ctx.DecodeByteArraySpan();
        IPEndPoint address = GetAddress(ip, ctx.DecodeInt());
        if (count > 3)
        {
            ctx.DecodeInt();
        }

        ReadOnlySpan<byte> id = ctx.DecodeByteArraySpan();
        return new Node(new PublicKey(id), address);
    };

    public NeighborsMsgSerializer(IEcdsa ecdsa,
        [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey,
        INodeIdResolver nodeIdResolver) : base(ecdsa, nodeKey, nodeIdResolver)
    {
    }

    public void Serialize(IByteBuffer byteBuffer, NeighborsMsg msg)
    {
        (int totalLength, int contentLength, int nodesContentLength) = GetLength(msg);

        byteBuffer.MarkIndex();
        PrepareBufferForSerialization(byteBuffer, totalLength, (byte)msg.MsgType);
        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);
        if (msg.Nodes.Count != 0)
        {
            stream.StartSequence(nodesContentLength);
            for (int i = 0; i < msg.Nodes.Count; i++)
            {
                Node node = msg.Nodes[i];
                SerializeNode(stream, node.Address, node.Id.Bytes);
            }
        }
        else
        {
            stream.Encode(Rlp.OfEmptySequence);
        }

        stream.Encode(msg.ExpirationTime);
        byteBuffer.ResetIndex();

        AddSignatureAndMdc(byteBuffer, totalLength + 1);
    }

    public NeighborsMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey FarPublicKey, _, IByteBuffer Data) = PrepareForDeserialization(msgBytes);

        NettyRlpStream rlp = new(Data);
        rlp.ReadSequenceLength();
        Node[] nodes = DeserializeNodes(rlp);

        long expirationTime = rlp.DecodeLong();
        NeighborsMsg msg = new(FarPublicKey, expirationTime, nodes);
        return msg;
    }

    private static Node[] DeserializeNodes(RlpStream rlpStream)
    {
        return rlpStream.DecodeArray<Node>(_decodeItem);
    }

    private static int GetNodesLength(ArraySegment<Node> nodes, out int contentLength)
    {
        contentLength = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            Node node = nodes[i];
            contentLength += Rlp.LengthOfSequence(GetLengthSerializeNode(node.Address, node.Id.Bytes));
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
            contentLength += Rlp.OfEmptySequence.Bytes.Length;
        }

        contentLength += Rlp.LengthOf(msg.ExpirationTime);

        return (Rlp.LengthOfSequence(contentLength), contentLength, nodesContentLength);
    }
}
