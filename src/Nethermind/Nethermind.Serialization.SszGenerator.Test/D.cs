using Nethermind.Merkleization;
using System.Collections.Generic;
using System.Linq;
using System;
using Nethermind.Int256;
using Nethermind.Serialization.SszGenerator.Test;

using SszLib = Nethermind.Serialization.Ssz.Ssz;

namespace Nethermind.Serialization;

public partial class SszEncoding2
{
    public static int GetLength(Test2 container)
    {
        return 1 + container.Selector switch
        {
            Test2Union.Type1 => 8,
            Test2Union.Type2 => 4,
            _ => 0,
        };
    }

    public static int GetLength(ICollection<Test2>? container)
    {
        if (container is null)
        {
            return 0;
        }
        int length = container.Count * 4;
        foreach (Test2 item in container)
        {
            length += GetLength(item);
        }
        return length;
    }

    public static ReadOnlySpan<byte> Encode(Test2 container)
    {
        Span<byte> buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }

    public static void Encode(Span<byte> data, Test2 container)
    {
        SszLib.Encode(data.Slice(0, 1), (byte)container.Selector);
        if (data.Length is 1)
        {
            return;
        }
        switch (container.Selector)
        {
            case Test2Union.Type1: SszLib.Encode(data.Slice(1), container.Type1); break;
            case Test2Union.Type2: SszLib.Encode(data.Slice(1), container.Type2); break;
        };
    }

    public static void Encode(Span<byte> data, ICollection<Test2> container)
    {
        int offset = container.Count * 4;
        int itemOffset = 0;
        foreach (Test2 item in container)
        {
            SszLib.Encode(data.Slice(itemOffset, 4), offset);
            itemOffset += 4;
            int length = GetLength(item);
            Encode(data.Slice(offset, length), item);
            offset += length;
        }
    }

    public static void Decode(ReadOnlySpan<byte> data, out Test2[] container)
    {
        if (data.Length is 0)
        {
            container = [];
            return;
        }
        int firstOffset = SszLib.DecodeInt(data.Slice(0, 4));
        int length = firstOffset / 4;
        container = new Test2[length];
        int index = 0;
        int offset = firstOffset;
        for (int nextOffsetIndex = 4; index < length - 1; index++, nextOffsetIndex += 4)
        {
            int nextOffset = SszLib.DecodeInt(data.Slice(nextOffsetIndex, 4));
            Decode(data.Slice(offset, nextOffset - offset), out container[index]);
            offset = nextOffset;
        }
        Decode(data.Slice(offset, length), out container[index]);
    }

    public static void Decode(ReadOnlySpan<byte> data, out Test2 container)
    {
        container = new();
        container.Selector = (Test2Union)data[0];
        switch (container.Selector)
        {
            case Test2Union.Type1: SszLib.Decode(data.Slice(1), out Int64 type1); container.Type1 = type1; break;
            case Test2Union.Type2: SszLib.Decode(data.Slice(1), out Int32 type2); container.Type2 = type2; break;
        };
    }

    public static void Merkleize(Test2 container, out UInt256 root)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(3));
        switch (container.Selector)
        {
            case Test2Union.Type1: merkleizer.Feed(container.Type1); ; break;
            case Test2Union.Type2: merkleizer.Feed(container.Type2); ; break;
        };
        merkleizer.CalculateRoot(out root);
        Merkle.MixIn(ref root, (byte)container.Selector);
    }

    public static void MerkleizeVector(IList<Test2> container, out UInt256 root)
    {
        UInt256[] subRoots = new UInt256[container.Count];
        for (int i = 0; i < container.Count; i++)
        {
            Merkleize(container[i], out subRoots[i]);
        }
        Merkle.Ize(out root, subRoots);
    }

    public static void MerkleizeList(IList<Test2> container, ulong limit, out UInt256 root)
    {
        MerkleizeVector(container, out root);
        Merkle.MixIn(ref root, container.Count);
    }
}
