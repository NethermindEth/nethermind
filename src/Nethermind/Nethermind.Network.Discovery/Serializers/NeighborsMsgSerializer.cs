// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            nodes is null ? Rlp.OfEmptySequence : Rlp.Encode(nodes),
            Rlp.Encode(msg.ExpirationTime)
        ).Bytes;

        byte[] serializedMsg = Serialize((byte)msg.MsgType, data);
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
}
