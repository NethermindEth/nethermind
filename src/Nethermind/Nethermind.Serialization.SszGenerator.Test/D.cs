using Nethermind.Merkleization;
using System.Collections.Generic;
using System.Linq;
using System;
using Nethermind.Int256;
using Nethermind.Serialization.SszGenerator.Test;

using SszLib = Nethermind.Serialization.Ssz.Ssz;
using static System.Runtime.InteropServices.JavaScript.JSType;

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

    public static int GetLength(ICollection<Test2> container)
    {
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
        switch (container.Selector)
        {
            case Test2Union.Type1: SszLib.Encode(data.Slice(0, 4), container.Type1); break;
            case Test2Union.Type2: SszLib.Encode(data.Slice(0, 4), container.Type2); break;
        }
    }

    public static void Decode(Span<byte> data, Test2 container)
    {
        SszLib.Encode(data.Slice(0, 1), (byte)data);
        switch (container.Selector)
        {
            case Test2Union.Type1: SszLib.Encode(data.Slice(0, 4), container.Type1); break;
            case Test2Union.Type2: SszLib.Encode(data.Slice(0, 4), container.Type2); break;
        }
    }

    public static void Merkleize(Test2 container, out UInt256 root)
    {
        SszLib.Encode(data.Slice(0, 1), (byte)data);
        switch (container.Selector)
        {
            case Test2Union.Type1: SszLib.Encode(data.Slice(0, 4), container.Type1); break;
            case Test2Union.Type2: SszLib.Encode(data.Slice(0, 4), container.Type2); break;
        }
    }
}
