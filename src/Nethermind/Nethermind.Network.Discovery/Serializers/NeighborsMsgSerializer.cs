//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Buffers;
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

        byte[] array = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            RlpStream stream = new(array);
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

            Serialize((byte)msg.MsgType, stream.Data.AsSpan(0, totalLength), byteBuffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
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
            int count = ctx.ReadNumberOfItemsRemaining(lastPosition);

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
