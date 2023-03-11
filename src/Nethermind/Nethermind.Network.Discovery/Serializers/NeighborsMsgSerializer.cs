// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Serializers;

public class NeighborsMsgSerializer : DiscoveryMsgSerializerBase, IZeroInnerMessageSerializer<NeighborsMsg>
{
    public NeighborsMsgSerializer(IEcdsa ecdsa,
        IPrivateKeyGenerator nodeKey,
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
        if (msg.Nodes.Any())
        {
            stream.StartSequence(nodesContentLength);
            for (int i = 0; i < msg.Nodes.Length; i++)
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
        (PublicKey FarPublicKey, Memory<byte> Mdc, IByteBuffer Data) results = PrepareForDeserialization(msgBytes);

        NettyRlpStream rlp = new(results.Data);
        rlp.ReadSequenceLength();
        Node[] nodes = DeserializeNodes(rlp) as Node[];

        long expirationTime = rlp.DecodeLong();
        NeighborsMsg msg = new(results.FarPublicKey, expirationTime, nodes);
        return msg;
    }

    private static Node?[] DeserializeNodes(RlpStream rlpStream)
    {
        return rlpStream.DecodeArray(ctx =>
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
        });
    }

    private int GetNodesLength(Node[] nodes, out int contentLength)
    {
        contentLength = 0;
        for (int i = 0; i < nodes.Length; i++)
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

    private (int totalLength, int contentLength, int nodesContentLength) GetLength(NeighborsMsg msg)
    {
        int nodesContentLength = 0;
        int contentLength = 0;
        if (msg.Nodes.Any())
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
