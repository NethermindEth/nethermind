// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Verkle;
using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.TreeNodes;

namespace Nethermind.Verkle.Tree.Serializers;

public class InternalNodeSerializer : IRlpStreamDecoder<InternalNode>, IRlpObjectDecoder<InternalNode>
{
    public static InternalNodeSerializer Instance => new();

    public Rlp Encode(InternalNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        var length = GetLength(item, rlpBehaviors);
        var stream = new RlpStream(Rlp.LengthOfSequence(length));
        stream.StartSequence(length);
        Encode(stream, item, rlpBehaviors);
        return new Rlp(stream.Data.ToArray());
    }

    public int GetLength(InternalNode item, RlpBehaviors rlpBehaviors)
    {
        return item.NodeType switch
        {
            VerkleNodeType.BranchNode => 1 + 66, // NodeType + InternalCommitment
            VerkleNodeType.StemNode => 1 + 32 + 66
                                       + (item.C1 == null ? 1 : 66)
                                       + (item.C2 == null ? 1 : 66), // NodeType + Stem + InternalCommitment + C1? + C2?
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public InternalNode Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        var nodeType = (VerkleNodeType)rlpStream.ReadByte();
        switch (nodeType)
        {
            case VerkleNodeType.BranchNode:
                InternalNode node = new(VerkleNodeType.BranchNode);
                node.UpdateCommitment(Banderwagon.FromBytesUncompressedUnchecked(rlpStream.DecodeByteArray(), isBigEndian: false));
                return node;
            case VerkleNodeType.StemNode:
                var stem = rlpStream.DecodeByteArray();

                var c1Ser = rlpStream.DecodeByteArray();
                Commitment? c1 = c1Ser.Length == 0
                    ? null
                    : new Commitment(Banderwagon.FromBytesUncompressedUnchecked(c1Ser, isBigEndian: false));

                var c2Ser = rlpStream.DecodeByteArray();
                Commitment? c2 = c2Ser.Length == 0
                    ? null
                    : new Commitment(Banderwagon.FromBytesUncompressedUnchecked(c2Ser, isBigEndian: false));

                Commitment extCommit =
                    new(Banderwagon.FromBytesUncompressedUnchecked(rlpStream.DecodeByteArray(), isBigEndian: false));
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
                stream.Encode(item.InternalCommitment.Point.ToBytesUncompressedLittleEndian());
                break;
            case VerkleNodeType.StemNode:
                stream.WriteByte((byte)VerkleNodeType.StemNode);
                stream.Encode(item.Stem!.Bytes);
                if (item.C1 is not null) stream.Encode(item.C1.Point.ToBytesUncompressedLittleEndian());
                else stream.EncodeEmptyByteArray();
                if (item.C2 is not null) stream.Encode(item.C2.Point.ToBytesUncompressedLittleEndian());
                else stream.EncodeEmptyByteArray();
                stream.Encode(item.InternalCommitment.Point.ToBytesUncompressedLittleEndian());
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public InternalNode Decode(byte[] data, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream stream = data.AsRlpStream();
        stream.ReadSequenceLength();
        return Decode(stream, rlpBehaviors);
    }
}
