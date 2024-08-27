using Nethermind.Int256;
using Nethermind.Merkleization;
using System;
using System.Collections.Generic;
using System.Linq;

using SszLib = Nethermind.Serialization.Ssz.Ssz;

namespace Nethermind.Serialization.SszGenerator.Test.Serialization;

public partial class SszEncoding2
{
    public static int GetLength(Test0 container)
    {
        return 16 +
               GetLength(container.Test2) +
               GetLength(container.Test3) +
               GetLength(container.Test4) +
               GetLength(container.Test5);
    }

    public static int GetLength(ICollection<Test0> container)
    {
        int length = container.Count * 4;
        foreach (Test0 item in container)
        {
            length += GetLength(item);
        }
        return length;
    }

    public static ReadOnlySpan<byte> Encode(Test0 container)
    {
        Span<byte> buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }

    public static void Encode(Span<byte> buf, Test0 container)
    {
        int dynOffset1 = 16;
        int dynOffset2 = dynOffset1 + GetLength(container.Test2);
        int dynOffset3 = dynOffset2 + GetLength(container.Test3);
        int dynOffset4 = dynOffset3 + GetLength(container.Test4);


        SszLib.Encode(buf.Slice(0, 4), dynOffset1);
        Encode(buf.Slice(4, 4), dynOffset2);
        Encode(buf.Slice(8, 4), dynOffset3);
        Encode(buf.Slice(12, 4), dynOffset4);

        if (container.Test2 is not null) SszLib.Encode(buf.Slice(dynOffset1, GetLength(container.Test2)), container.Test2);
        if (container.Test3 is not null) SszLib.Encode(buf.Slice(dynOffset2, GetLength(container.Test3)), container.Test3);
        if (container.Test4 is not null) SszLib.Encode(buf.Slice(dynOffset3, GetLength(container.Test4)), container.Test4);
        if (container.Test5 is not null) SszLib.Encode(buf.Slice(dynOffset4, GetLength(container.Test5)), container.Test5);


    }

    public static void Encode(Span<byte> buf, ICollection<Test0> container)
    {
        int offset = container.Count * 4;
        int itemOffset = 0;
        foreach (Test0 item in container)
        {
            Encode(buf.Slice(itemOffset, 4), offset);
            itemOffset += 4;
            int length = GetLength(item);
            Encode(buf.Slice(offset, length), item);
            offset += length;
        }
    }

    public static void Decode(ReadOnlySpan<byte> data, out Test0 container)
    {
        container = new();
        int dynOffset1 = 16;

        int dynOffset2 = SszLib.DecodeInt(data.Slice(0, 4));
        int dynOffset3 = SszLib.DecodeInt(data.Slice(4, 4));
        int dynOffset4 = SszLib.DecodeInt(data.Slice(8, 4));
        int dynOffset5 = SszLib.DecodeInt(data.Slice(12, 4));
        if (dynOffset2 - dynOffset1 > 0) Decode(data.Slice(dynOffset1, dynOffset2 - dynOffset1), out container.Test2);
        if (dynOffset3 - dynOffset2 > 0) Decode(data.Slice(dynOffset2, dynOffset3 - dynOffset2), out container.Test3);
        if (dynOffset4 - dynOffset3 > 0) Decode(data.Slice(dynOffset3, dynOffset4 - dynOffset3), out container.Test4);
        if (data.Length - dynOffset4 > 0) Decode(data.Slice(dynOffset4, data.Length - dynOffset4), out container.Test5);


    }

    public static void Decode(ReadOnlySpan<byte> data, out Test0[] container)
    {
        if (data.Length is 0)
        {
            container = [];
            return;
        }

        int firstOffset = SszLib.DecodeInt(data.Slice(0, 4));
        int length = firstOffset / 4;

        container = new Test0[length];

        int index = 0;
        int offset = firstOffset;
        for (int index = 0, nextOffsetIndex = 4; index < length - 1; index++, nextOffsetIndex += 4)
        {
            int nextOffset = SszLib.DecodeInt(data.Slice(nextOffsetIndex, 4))
            Decode(data.Slice(offset, nextOffset - offset), out container[index]);
            offset = nextOffset;
        }
        Decode(data.Slice(offset, length), out container[index]);
    }

    public static void Decode(ReadOnlySpan<byte> data, out List<Test0> container)
    {
        Decode(data, out Test0[] array);
        container = array.ToList();
    }

    public static void Merkleize(Test0 container, out UInt256 root)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(4));

        Merkleize(container.Test2, out UInt256 rootOfTest2);
        merkleizer.Feed(rootOfTest2);
        Merkleize(container.Test3, out UInt256 rootOfTest3);
        merkleizer.Feed(rootOfTest3);
        Merkleize(container.Test4, out UInt256 rootOfTest4);
        merkleizer.Feed(rootOfTest4);
        Merkleize(container.Test5, out UInt256 rootOfTest5);
        merkleizer.Feed(rootOfTest5);

        merkleizer.CalculateRoot(out root);
    }

    public static void Merkleize(ICollection<Test0> container, out UInt256 root)
    {
        Merkleize(container, (ulong)container.Count, out root);
    }

    public static void Merkleize(ICollection<Test0> container, ulong limit, out UInt256 root)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(limit));

        foreach (Test0 item in container)
        {
            Merkleize(item, out UInt256 localRoot);
            merkleizer.Feed(localRoot);
        }

        merkleizer.CalculateRoot(out root);
    }
}
