// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Verkle.Tree.Serializers;


public class LeafStoreSerializer
{
    public static LeafStoreSerializer Instance => new();
    public int GetLength(LeafStoreInterface item, RlpBehaviors rlpBehaviors)
    {
        int length = Rlp.LengthOf(item.Count);
        foreach (KeyValuePair<byte[], byte[]?> pair in item)
        {
            length += Rlp.LengthOf(pair.Key);
            length += Rlp.LengthOf(pair.Value);
        }
        return length;
    }

    public LeafStore Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        LeafStore item = new(Bytes.SpanEqualityComparer);
        int length = rlpStream.DecodeInt();
        for (int i = 0; i < length; i++)
        {
            item[rlpStream.DecodeByteArray()] = rlpStream.DecodeByteArray();
        }
        return item;
    }

    public void Encode(RlpStream stream, LeafStoreInterface item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item.Count);
        foreach (KeyValuePair<byte[], byte[]?> pair in item)
        {
            stream.Encode(pair.Key.AsSpan());
            stream.Encode(pair.Value.AsSpan());
        }
    }
}
