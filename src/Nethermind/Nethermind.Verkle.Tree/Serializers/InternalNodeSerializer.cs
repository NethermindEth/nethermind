// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Utils;

namespace Nethermind.Verkle.Tree.Serializers;

public class InternalNodeSerializer : IRlpStreamDecoder<InternalNode>, IRlpObjectDecoder<InternalNode>
{
    public static InternalNodeSerializer Instance => new InternalNodeSerializer();
    public int GetLength(InternalNode item, RlpBehaviors rlpBehaviors)
    {
        return item.NodeType switch
        {
            NodeType.BranchNode => 32 + 1,
            NodeType.StemNode => 32 + 31 + 1,
            var _ => throw new ArgumentOutOfRangeException()
        };
    }

    public InternalNode Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        NodeType nodeType = (NodeType)rlpStream.ReadByte();
        switch (nodeType)
        {
            case NodeType.BranchNode:
                BranchNode node = new BranchNode();
                node.UpdateCommitment(new Banderwagon(rlpStream.Read(32).ToArray()));
                return node;
            case NodeType.StemNode:
                return new StemNode(rlpStream.Read(31).ToArray(), new Commitment(new Banderwagon(rlpStream.Read(32).ToArray())));
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
    public void Encode(RlpStream stream, InternalNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        switch (item.NodeType)
        {
            case NodeType.BranchNode:
                stream.WriteByte((byte)NodeType.BranchNode);
                stream.Write(item._internalCommitment.Point.ToBytes());
                break;
            case NodeType.StemNode:
                stream.WriteByte((byte)NodeType.StemNode);
                stream.Write(item.Stem);
                stream.Write(item._internalCommitment.Point.ToBytes());
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
    public Rlp Encode(InternalNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = GetLength(item, rlpBehaviors);
        RlpStream stream = new RlpStream(Rlp.LengthOfSequence(length));
        stream.StartSequence(length);
        Encode(stream, item, rlpBehaviors);
        return new Rlp(stream.Data);
    }
    public InternalNode Decode(byte[] data, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream stream = data.AsRlpStream();
        stream.ReadSequenceLength();
        return Decode(stream, rlpBehaviors);
    }

}
