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

using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Serializers;

public class NeighborsMsgSerializer : DiscoveryMsgSerializerBase, IMessageSerializer<NeighborsMsg>
{
    public NeighborsMsgSerializer(IEcdsa ecdsa,
        IPrivateKeyGenerator nodeKey,
        INodeIdResolver nodeIdResolver) : base(ecdsa, nodeKey, nodeIdResolver)
    {
    }

    public byte[] Serialize(NeighborsMsg msg)
    {
        Rlp[]? nodes = null;
        if (msg.Nodes.Any())
        {
            nodes = new Rlp[msg.Nodes.Length];
            for (int i = 0; i < msg.Nodes.Length; i++)
            {
                Node node = msg.Nodes[i];
                Rlp serializedNode = SerializeNode(node.Address, node.Id.Bytes);
                nodes[i] = serializedNode;
            }
        }

        byte[] data = Rlp.Encode(
            nodes == null ? Rlp.OfEmptySequence : Rlp.Encode(nodes),
            Rlp.Encode(msg.ExpirationTime)
        ).Bytes;

        byte[] serializedMsg = Serialize((byte) msg.MsgType, data);
        return serializedMsg;
    }

    public NeighborsMsg Deserialize(byte[] msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, byte[] Data) results = PrepareForDeserialization(msgBytes);

        RlpStream rlp = results.Data.AsRlpStream();
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
            return new Node(new PublicKey(id), address, false);
        });
    }
}
