// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree.Serializers;


public class SuffixTreeSerializer : IRlpStreamDecoder<SuffixTree>, IRlpObjectDecoder<SuffixTree>
{
    public static SuffixTreeSerializer Instance => new SuffixTreeSerializer();
    public int GetLength(SuffixTree item, RlpBehaviors rlpBehaviors)
    {
        return 31 + 32 + 32 + 32;
    }

    public int GetLength(SuffixTree item, RlpBehaviors rlpBehaviors, out int contentLength)
    {
        contentLength = GetLength(item, rlpBehaviors);
        return Rlp.LengthOfSequence(contentLength);
    }

    public SuffixTree Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        byte[] stem = rlpStream.Read(31).ToArray();
        byte[] c1 = rlpStream.Read(32).ToArray();
        byte[] c2 = rlpStream.Read(32).ToArray();
        byte[] extCommit = rlpStream.Read(32).ToArray();
        return new SuffixTree(stem, c1, c2, extCommit);
    }
    public void Encode(RlpStream stream, SuffixTree item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Write(item.Stem);
        stream.Write(item.C1.Point.ToBytes());
        stream.Write(item.C2.Point.ToBytes());
        stream.Write(item.ExtensionCommitment.Point.ToBytes());
    }
    public Rlp Encode(SuffixTree item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = GetLength(item, rlpBehaviors);
        RlpStream stream = new RlpStream(Rlp.LengthOfSequence(length));
        stream.StartSequence(length);
        Encode(stream, item, rlpBehaviors);
        return new Rlp(stream.Data);
    }

    public SuffixTree Decode(byte[] data, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream stream = data.AsRlpStream();
        stream.ReadSequenceLength();
        return Decode(stream, rlpBehaviors);
    }
}
