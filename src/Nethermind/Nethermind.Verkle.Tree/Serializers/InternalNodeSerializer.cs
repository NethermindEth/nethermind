// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Verkle;
using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree.Serializers;

public class InternalNodeSerializer : IRlpStreamDecoder<InternalNode>, IRlpObjectDecoder<InternalNode>
{
    public static InternalNodeSerializer Instance => new InternalNodeSerializer();
    public int GetLength(InternalNode item, RlpBehaviors rlpBehaviors)
    {
        return item.NodeType switch
        {
            VerkleNodeType.BranchNode => 1 + 33, // NodeType + InternalCommitment
            VerkleNodeType.StemNode => 1 + 32 + 33
                                       + (item.C1 == null ? 1 : 33)
                                       + (item.C2 == null ? 1 : 33), // NodeType + Stem + InternalCommitment + C1? + C2?
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public InternalNode Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        VerkleNodeType nodeType = (VerkleNodeType)rlpStream.ReadByte();
        switch (nodeType)
        {
            case VerkleNodeType.BranchNode:
                InternalNode node = new(VerkleNodeType.BranchNode);
                node.UpdateCommitment(Banderwagon.FromBytes(rlpStream.DecodeByteArray(), subgroupCheck: false)!.Value);
                return node;
            case VerkleNodeType.StemNode:
                byte[] stem = rlpStream.DecodeByteArray();

                byte[] c1Ser = rlpStream.DecodeByteArray();
                Commitment? c1 = c1Ser.Length == 0
                    ? null
                    : new (Banderwagon.FromBytes(c1Ser, subgroupCheck: false)!.Value);

                byte[] c2Ser = rlpStream.DecodeByteArray();
                Commitment? c2 = c2Ser.Length == 0
                    ? null
                    : new (Banderwagon.FromBytes(c2Ser, subgroupCheck: false)!.Value);

                Commitment extCommit =
                    new (Banderwagon.FromBytes(rlpStream.DecodeByteArray(), subgroupCheck: false)!.Value);
                return new InternalNode(VerkleNodeType.StemNode, stem, c1, c2, extCommit);
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
    public void Encode(RlpStream stream, InternalNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        switch (item.NodeType)
        {
            case VerkleNodeType.BranchNode:
                stream.WriteByte((byte)VerkleNodeType.BranchNode);
                stream.Encode(item.InternalCommitment.Point.ToBytes());
                break;
            case VerkleNodeType.StemNode:
                stream.WriteByte((byte)VerkleNodeType.StemNode);
                stream.Encode(item.Stem!.Bytes);
                if (item.C1 is not null) stream.Encode(item.C1.Point.ToBytes());
                else stream.EncodeEmptyByteArray();
                if (item.C2 is not null) stream.Encode(item.C2.Point.ToBytes());
                else stream.EncodeEmptyByteArray();
                stream.Encode(item.InternalCommitment.Point.ToBytes());
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
