// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree.Serializers;


public class BranchStoreSerializer : IRlpStreamDecoder<BranchStore>
{
    private static InternalNodeSerializer InternalNodeSerializer => InternalNodeSerializer.Instance;

    public static BranchStoreSerializer Instance => new BranchStoreSerializer();
    public int GetLength(BranchStore item, RlpBehaviors rlpBehaviors)
    {
        int length = Rlp.LengthOf(item.Count);
        foreach (KeyValuePair<byte[], InternalNode?> pair in item)
        {
            length += Rlp.LengthOf(pair.Key);
            length += pair.Value == null ? Rlp.EmptyArrayByte : InternalNodeSerializer.GetLength(pair.Value, RlpBehaviors.None);
        }
        return length;
    }

    public BranchStore Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        BranchStore item = new BranchStore();
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
                item[key] = InternalNodeSerializer.Decode(rlpStream);
            }
        }
        return item;
    }
    public void Encode(RlpStream stream, BranchStore item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item.Count);
        foreach (KeyValuePair<byte[], InternalNode?> pair in item)
        {
            stream.Encode(pair.Key);
            if (pair.Value is null) stream.EncodeEmptyByteArray();
            else InternalNodeSerializer.Encode(stream, pair.Value);
        }
    }
}
