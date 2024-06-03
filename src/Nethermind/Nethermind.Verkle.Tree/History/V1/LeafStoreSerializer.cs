// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Verkle.Tree.History.V1;

public class LeafStoreSerializer
{
    public static LeafStoreSerializer Instance => new();

    public int GetLength(LeafStoreInterface item, RlpBehaviors rlpBehaviors)
    {
        var length = Rlp.LengthOf(item.Count);
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
        var length = rlpStream.DecodeInt();
        for (var i = 0; i < length; i++) item[rlpStream.DecodeByteArray()] = rlpStream.DecodeByteArray();
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
