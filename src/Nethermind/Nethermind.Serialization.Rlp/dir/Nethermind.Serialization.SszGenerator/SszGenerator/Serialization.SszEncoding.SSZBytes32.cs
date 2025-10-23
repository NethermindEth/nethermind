using Nethermind.Merkleization;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using System;
using System.Collections;

using SszLib = Nethermind.Serialization.Ssz.Ssz;

namespace Nethermind.Serialization;

public partial class SszEncoding
{
    public static int GetLength(SSZBytes32 container)
    {

        return 32;
    }

    public static int GetLength(ICollection<SSZBytes32>? container)
    {
        if(container is null)
        {
            return 0;
        }

        return container.Count * 32;
    }

    public static byte[] Encode(SSZBytes32 container)
    {
        byte[] buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }

    public static void Encode(Span<byte> data, SSZBytes32 container)
    {


        SszLib.Encode(data.Slice(0, 32), container.Data);

    }

    public static byte[] Encode(ICollection<SSZBytes32>? items)
    {
        if (items is null)
        {
            return [];
        }
        byte[] buf = new byte[GetLength(items)];
        Encode(buf, items);
        return buf;
    }

    public static void Encode(Span<byte> data, ICollection<SSZBytes32>? items)
    {
        if(items is null) return;

        int offset = 0;
        foreach(SSZBytes32 item in items)
        {
            int length = GetLength(item);
            Encode(data.Slice(offset, length), item);
            offset += length;
        }
    }

    public static void Decode(ReadOnlySpan<byte> data, out SSZBytes32 container)
    {
        container = new();

        SszLib.Decode(data.Slice(0, 32), out ReadOnlySpan<Byte> _data); container.Data = [ .._data];

    }

    public static void Decode(ReadOnlySpan<byte> data, out SSZBytes32[] container)
    {
        if(data.Length is 0)
        {
            container = [];
            return;
        }

        int length = data.Length / 32;
        container = new SSZBytes32[length];

        int offset = 0;
        for(int index = 0; index < length; index++)
        {
            Decode(data.Slice(offset, 32), out container[index]);
            offset += 32;
        }
    }

    public static void Merkleize(SSZBytes32 container, out UInt256 root)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(1));
        merkleizer.Feed(container.Data);
        merkleizer.CalculateRoot(out root);
    }

    public static void MerkleizeVector(IList<SSZBytes32>? container, out UInt256 root)
    {
        if(container is null)
        {
            root = 0;
            return;
        }

        UInt256[] subRoots = new UInt256[container.Count];
        for(int i = 0; i < container.Count; i++)
        {
            Merkleize(container[i], out subRoots[i]);
        }

        Merkle.Merkleize(out root, subRoots);
    }

    public static void MerkleizeList(IList<SSZBytes32>? container, ulong limit, out UInt256 root)
    {
        if(container is null || container.Count is 0)
        {
            root = 0;
            Merkle.MixIn(ref root, (int)limit);
            return;
        }

        MerkleizeVector(container, out root);
        Merkle.MixIn(ref root, container.Count);
    }
}