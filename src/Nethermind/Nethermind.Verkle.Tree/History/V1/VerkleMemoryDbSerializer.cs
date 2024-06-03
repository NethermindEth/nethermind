// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Tree.Serializers;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.History.V1;

public class VerkleMemoryDbSerializer
{
    public static VerkleMemoryDbSerializer Instance => new();

    public int GetLength(VerkleMemoryDb item, RlpBehaviors rlpBehaviors)
    {
        var length = 0;
        length += LeafStoreSerializer.Instance.GetLength(item.LeafTable, RlpBehaviors.None);
        length += InternalStoreSerializer.Instance.GetLength(item.InternalTable, RlpBehaviors.None);
        return length;
    }

    public int GetLength(SortedVerkleMemoryDb item, RlpBehaviors rlpBehaviors)
    {
        var length = 0;
        length += LeafStoreSerializer.Instance.GetLength(item.LeafTable, RlpBehaviors.None);
        length += InternalStoreSerializer.Instance.GetLength(item.InternalTable, RlpBehaviors.None);
        return length;
    }

    public VerkleMemoryDb Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return new VerkleMemoryDb(
            LeafStoreSerializer.Instance.Decode(rlpStream),
            InternalStoreSerializer.Instance.Decode(rlpStream)
        );
    }

    public void Encode(RlpStream stream, VerkleMemoryDb item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        LeafStoreSerializer.Instance.Encode(stream, item.LeafTable);
        InternalStoreSerializer.Instance.Encode(stream, item.InternalTable);
    }

    public void Encode(RlpStream stream, SortedVerkleMemoryDb item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        LeafStoreSerializer.Instance.Encode(stream, item.LeafTable);
        InternalStoreSerializer.Instance.Encode(stream, item.InternalTable);
    }
}
