//using Nethermind.Int256;
//using Nethermind.Merkleization;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using Nethermind.Serialization.SszGenerator.Test;

//using SszLib = Nethermind.Serialization.Ssz.Ssz;

//namespace Nethermind.Serialization;

//public partial class SszEncoding
//{
//    public static int GetLength(IdentityPreimage container)
//    {
//        return 52;
//    }

//    public static int GetLength(ICollection<IdentityPreimage> container)
//    {
//        return container.Count * 52;
//    }

//    public static ReadOnlySpan<byte> Encode(IdentityPreimage container)
//    {
//        Span<byte> buf = new byte[GetLength(container)];
//        Encode(buf, container);
//        return buf;
//    }

//    public static void Encode(Span<byte> buf, IdentityPreimage container)
//    {

//        SszLib.Encode(buf.Slice(0, 0), container.Data);


//    }

//    public static void Encode(Span<byte> buf, ICollection<IdentityPreimage> container)
//    {
//        int offset = 0;
//        foreach (IdentityPreimage item in container)
//        {
//            int length = GetLength(item);
//            Encode(buf.Slice(offset, length), item);
//            offset += length;
//        }
//    }

//    public static void Decode(ReadOnlySpan<byte> data, out IdentityPreimage container)
//    {
//        container = new();

//        SszLib.Decode(data.Slice(0, 0), out byte[] value); container.Data = value;

//    }

//    public static void Decode(ReadOnlySpan<byte> data, out IdentityPreimage[] container)
//    {
//        if (data.Length is 0)
//        {
//            container = [];
//            return;
//        }

//        int length = data.Length / 52;

//        container = new IdentityPreimage[length];

//        int offset = 0;
//        for (int index = 0; index < length; index++)
//        {
//            Decode(data.Slice(offset, 52), out container[index]);
//            offset += 52;
//        }
//    }

//    public static void Decode(ReadOnlySpan<byte> data, out List<IdentityPreimage> container)
//    {
//        Decode(data, out IdentityPreimage[] array);
//        container = array.ToList();
//    }

//    public static void Merkleize(IdentityPreimage container, out UInt256 root)
//    {
//        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(1));

//        merkleizer.Feed(container.Data);

//        merkleizer.CalculateRoot(out root);
//    }

//    public static void MerkleizeList(ICollection<IdentityPreimage> container, ulong limit, out UInt256 root)
//    {
//        MerkleizeVector(container, limit, out root);
//        Merkle.MixIn(ref root, (int)limit);
//    }

//    public static void MerkleizeVector(ICollection<IdentityPreimage> container, ulong limit, out UInt256 root)
//    {
//        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(limit));

//        foreach (IdentityPreimage item in container)
//        {
//            Merkleize(item, out UInt256 localRoot);
//            merkleizer.Feed(localRoot);
//        }

//        merkleizer.CalculateRoot(out root);
//    }
//}
