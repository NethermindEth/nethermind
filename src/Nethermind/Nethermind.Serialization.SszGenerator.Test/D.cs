//using Nethermind.Int256;
//using Nethermind.Merkleization;
//using Nethermind.Serialization.Ssz;
//using System;

//namespace Nethermind.Serialization.SszGenerator.Test.Generated;

//public partial class BasicSzzStructSszSerializer2
//{
//    public int GetLength(SszTests.BasicSzzStruct container)
//    {
//        return 16 +
//               (container.NumberOrNull.HasValue ? 4 : 0) +
//               (container.Array?.Length ?? 0) +
//               (container.ArrayOrNull?.Length ?? 0);
//    }

//    public ReadOnlySpan<byte> Serialize(SszTests.BasicSzzStruct container)
//    {
//        Span<byte> buf = new byte[GetLength(container)];

//        int dynOffset1 = 16;
//        int dynOffset2 = dynOffset1 + (container.NumberOrNull.HasValue ? 4 : 0);
//        int dynOffset3 = dynOffset2 + (container.Array?.Length ?? 0);

//        Ssz.Ssz.Encode(buf.Slice(0, 4), container.Number);
//        Ssz.Ssz.Encode(buf.Slice(4, 4), dynOffset1);
//        Ssz.Ssz.Encode(buf.Slice(8, 4), dynOffset2);
//        Ssz.Ssz.Encode(buf.Slice(12, 4), dynOffset3);

//        if (container.NumberOrNull is not null) Ssz.Ssz.Encode(buf.Slice(dynOffset1, (container.NumberOrNull.HasValue ? 4 : 0)), container.NumberOrNull.Value);
//        if (container.Array is not null) Ssz.Ssz.Encode(buf.Slice(dynOffset2, (container.Array?.Length ?? 0)), container.Array);
//        if (container.ArrayOrNull is not null) Ssz.Ssz.Encode(buf.Slice(dynOffset3, (container.ArrayOrNull?.Length ?? 0)), container.ArrayOrNull);

//        return buf;
//    }

//    public SszTests.BasicSzzStruct Deserialize(ReadOnlySpan<byte> data)
//    {
//        SszTests.BasicSzzStruct container = new();

//        int dynOffset1 = 16;

//        container.Number = Ssz.Ssz.DecodeInt(data.Slice(0, 4));
//        int dynOffset2 = Ssz.Ssz.DecodeInt(data.Slice(4, 4));
//        int dynOffset3 = Ssz.Ssz.DecodeInt(data.Slice(8, 4));
//        int dynOffset4 = Ssz.Ssz.DecodeInt(data.Slice(12, 4));

//        if (dynOffset2 - dynOffset1 > 0) container.NumberOrNull = Ssz.Ssz.DecodeInt(data.Slice(dynOffset1, dynOffset2 - dynOffset1));
//        if (dynOffset3 - dynOffset2 > 0) container.Array = Ssz.Ssz.DecodeBytes(data.Slice(dynOffset2, dynOffset3 - dynOffset2));
//        if (data.Length - dynOffset3 > 0) container.ArrayOrNull = Ssz.Ssz.DecodeBytes(data.Slice(dynOffset3, data.Length - dynOffset3));

//        return container;
//    }

//    public static void Merkleize(out UInt256 root, SszTests.BasicSzzStruct container)
//    {
//        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(4));

//        merkleizer.Feed(container.Number);
//        merkleizer.Feed(container.NumberOrNull);
//        merkleizer.Feed(container.Array);
//        merkleizer.Feed(container.ArrayOrNull);

//        merkleizer.CalculateRoot(out root);
//    }
//}
