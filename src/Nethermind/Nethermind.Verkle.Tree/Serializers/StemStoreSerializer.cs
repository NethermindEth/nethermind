// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree.Serializers;


public class StemStoreSerializer : IRlpStreamDecoder<StemStore>
{
    private static SuffixTreeSerializer SuffixTreeSerializer => SuffixTreeSerializer.Instance;

    public static StemStoreSerializer Instance => new StemStoreSerializer();

    public int GetLength(StemStore item, RlpBehaviors rlpBehaviors)
    {
        int length = Rlp.LengthOf(item.Count);
        foreach (KeyValuePair<byte[], SuffixTree?> pair in item)
        {
            length += Rlp.LengthOf(pair.Key);
            length += pair.Value == null ? Rlp.EmptyArrayByte : SuffixTreeSerializer.GetLength(pair.Value, RlpBehaviors.None);
        }
        return length;
    }

    public StemStore Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        StemStore item = new StemStore();
        int length = rlpStream.DecodeInt();
        for (int i = 0; i < length; i++)
        {
            byte[] key = rlpStream.DecodeByteArray();
            if (rlpStream.PeekNextItem().Length == 0)
            {
                item[key] = null;
                rlpStream.SkipItem();
            }
            else
            {
                item[key] = SuffixTreeSerializer.Decode(rlpStream);
            }
        }
        return item;
    }
    public void Encode(RlpStream stream, StemStore item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item.Count);
        foreach (KeyValuePair<byte[], SuffixTree?> pair in item)
        {
            stream.Encode(pair.Key);
            if (pair.Value is null) stream.EncodeEmptyByteArray();
            else SuffixTreeSerializer.Encode(stream, pair.Value);
        }
    }
}
