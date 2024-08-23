using Nethermind.Int256;
using Nethermind.Merkleization;
using Nethermind.Serialization.Ssz;
using System;

namespace Nethermind.Serialization.SszGenerator.Test.Generated;


public partial class Ssz
{
    public static int GetLength(SszTests.BasicSzzClass container)
    {
        return 16;
    }

    public static ReadOnlySpan<byte> Serialize(SszTests.BasicSzzClass container)
    {
        Span<byte> buf = new byte[GetLength(container)];

        Serialize(buf.Slice(0, 16), container.FixedStruct);

        return buf;
    }

    public static SszTests.BasicSzzClass Deserialize(ReadOnlySpan<byte> data)
    {
        SszTests.BasicSzzClass container = new();

        container.FixedStruct = Deserialize(data.Slice(0, 16));

        return container;
    }

    public static void Merkleize(out UInt256 root, SszTests.BasicSzzClass container)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(1));

        Merkleize(out UInt256 rootOfFixedStruct, in container.FixedStruct);
        merkleizer.Feed(rootOfFixedStruct);

        merkleizer.CalculateRoot(out root);
    }
}
public partial class Ssz
{
    public static int GetLength(ref SszTests.StaticStruct container)
    {
        return 16;
    }

    public static ReadOnlySpan<byte> Serialize(ref SszTests.StaticStruct container)
    {
        Span<byte> buf = new byte[GetLength(ref container)];

        Ssz.Ssz.Encode(buf.Slice(0, 8), container.X);
        Ssz.Ssz.Encode(buf.Slice(8, 8), container.Y);

        return buf;
    }

    public static ref SszTests.StaticStruct Deserialize(ReadOnlySpan<byte> data)
    {
        ref SszTests.StaticStruct container = new();

        container.X = Ssz.Ssz.DecodeInt(data.Slice(0, 8));
        container.Y = Ssz.Ssz.DecodeInt(data.Slice(8, 8));

        return container;
    }

    public static void Merkleize(out UInt256 root, in SszTests.StaticStruct container)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(2));

        merkleizer.Feed(container.X);
        merkleizer.Feed(container.Y);

        merkleizer.CalculateRoot(out root);
    }
}

