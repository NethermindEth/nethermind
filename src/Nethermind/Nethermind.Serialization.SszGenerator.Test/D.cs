//using Nethermind.Int256;
//using Nethermind.Merkleization;
//using System;

//using SszLib = Nethermind.Serialization.Ssz.Ssz;

//namespace Nethermind.Serialization.SszGenerator.Test.Serialization;

//public partial class SszEncoding
//{
//    public static int GetLength(SszTests.StaticStruct container)
//    {
//        return 16;
//    }


//    public static ReadOnlySpan<byte> Encode(SszTests.StaticStruct container)
//    {
//        Span<byte> buf = new byte[GetLength(container)];
//        Encode(buf, container);
//        return buf;
//    }

//    public static void Encode(Span<byte> buf, SszTests.StaticStruct container)
//    {
//        SszLib.Encode(buf.Slice(0, 8), container.X);
//        SszLib.Encode(buf.Slice(8, 8), container.Y);
//    }

//    public static void Decode(ReadOnlySpan<byte> data, out SszTests.StaticStruct container)
//    {
//        container = new();

//        container.X = SszLib.DecodeLong(data.Slice(0, 8));
//        container.Y = SszLib.DecodeLong(data.Slice(8, 8));

//    }

//    public static void Merkleize(SszTests.StaticStruct container, out UInt256 root)
//    {
//        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(2));

//        merkleizer.Feed(container.X);
//        merkleizer.Feed(container.Y);

//        merkleizer.CalculateRoot(out root);
//    }
//}



//public partial class SszEncoding
//{
//    public static int GetLength(SszTests.BasicSzzClass container)
//    {
//        return 16;
//    }


//    public static ReadOnlySpan<byte> Encode(SszTests.BasicSzzClass container)
//    {
//        Span<byte> buf = new byte[GetLength(container)];
//        Encode(buf, container);
//        return buf;
//    }

//    public static void Encode(Span<byte> buf, SszTests.BasicSzzClass container)
//    {
//        Encode(buf.Slice(0, 16), container.FixedStruct);
//    }

//    public static void Decode(ReadOnlySpan<byte> data, out SszTests.BasicSzzClass container)
//    {
//        container = new();

//        Decode(data.Slice(0, 16), out SszTests.StaticStruct fixedStruct); container.FixedStruct = fixedStruct;

//    }

//    public static void Merkleize(SszTests.BasicSzzClass container, out UInt256 root)
//    {
//        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(1));

//        Merkleize(container.FixedStruct, out UInt256 rootOfFixedStruct);
//        merkleizer.Feed(rootOfFixedStruct);

//        merkleizer.CalculateRoot(out root);
//    }
//}
