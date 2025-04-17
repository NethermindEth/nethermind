//using Nethermind.Merkleization;
//using System.Collections.Generic;
//using Nethermind.Int256;
//using Nethermind.Merge.Plugin.Data;
//using System;

//namespace Nethermind.Serialization;

//public partial class SszEncoding2
//{
//    public static int GetLength(BlobAndProofV2? container)
//    {
//        if (container is null)
//        {
//            return 0;
//        }

//        return 6144;
//    }

//    public static int GetLength(ICollection<BlobAndProofV2>? container)
//    {
//        if (container is null)
//        {
//            return 0;
//        }

//        return container.Count * 6144;
//    }

//    public static byte[] Encode(BlobAndProofV2? container)
//    {
//        if (container is null)
//        {
//            return [];
//        }
//        byte[] buf = new byte[GetLength(container)];
//        Encode(buf, container);
//        return buf;
//    }

//    public static void Encode(Span<byte> data, BlobAndProofV2? container)
//    {
//        if (container is null)
//        {
//            return;
//        }

//        Encode(data.Slice(0, 110), container.Blob);
//        Encode(data.Slice(0, 6144), container.Proofs);
//    }

//    public static byte[] Encode(ICollection<BlobAndProofV2>? items)
//    {
//        if (items is null)
//        {
//            return [];
//        }
//        byte[] buf = new byte[GetLength(items)];
//        Encode(buf, items);
//        return buf;
//    }

//    public static void Encode(Span<byte> data, ICollection<BlobAndProofV2>? items)
//    {
//        if (items is null) return;

//        int offset = 0;
//        foreach (BlobAndProofV2 item in items)
//        {
//            int length = GetLength(item);
//            Encode(data.Slice(offset, length), item);
//            offset += length;
//        }
//    }

//    public static void Decode(ReadOnlySpan<byte> data, out BlobAndProofV2 container)
//    {
//        container = new();


//        Decode(data.Slice(0, 0), out Blob blob); container.Blob = blob;
//        Decode(data.Slice(0, 6144), out Proof[] proofs); container.Proofs = [.. proofs];

//    }

//    public static void Decode(ReadOnlySpan<byte> data, out BlobAndProofV2[] container)
//    {
//        if (data.Length is 0)
//        {
//            container = [];
//            return;
//        }

//        int length = data.Length / 6144;
//        container = new BlobAndProofV2[length];

//        int offset = 0;
//        for (int index = 0; index < length; index++)
//        {
//            Decode(data.Slice(offset, 6144), out container[index]);
//            offset += 6144;
//        }
//    }

//    public static void Merkleize(BlobAndProofV2? container, out UInt256 root)
//    {
//        if (container is null)
//        {
//            root = 0;
//            return;
//        }
//        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(2));
//        Merkleize(container.Blob, out UInt256 blobRoot); merkleizer.Feed(blobRoot);
//        MerkleizeVector(container.Proofs, out UInt256 proofsRoot); merkleizer.Feed(proofsRoot);
//        merkleizer.CalculateRoot(out root);
//    }

//    public static void MerkleizeVector(IList<BlobAndProofV2>? container, out UInt256 root)
//    {
//        if (container is null)
//        {
//            root = 0;
//            return;
//        }

//        UInt256[] subRoots = new UInt256[container.Count];
//        for (int i = 0; i < container.Count; i++)
//        {
//            Merkleize(container[i], out subRoots[i]);
//        }

//        Merkle.Merkleize(out root, subRoots);
//    }

//    public static void MerkleizeList(IList<BlobAndProofV2>? container, ulong limit, out UInt256 root)
//    {
//        if (container is null || container.Count is 0)
//        {
//            root = 0;
//            Merkle.MixIn(ref root, (int)limit);
//            return;
//        }

//        MerkleizeVector(container, out root);
//        Merkle.MixIn(ref root, container.Count);
//    }
//}

//public partial class SszEncoding2
//{
//    public static int GetLength(BlobVersionedHash container)
//    {

//        return 32;
//    }

//    public static int GetLength(ICollection<BlobVersionedHash>? container)
//    {
//        if (container is null)
//        {
//            return 0;
//        }

//        return container.Count * 32;
//    }

//    public static byte[] Encode(BlobVersionedHash container)
//    {
//        byte[] buf = new byte[GetLength(container)];
//        Encode(buf, container);
//        return buf;
//    }

//    public static void Encode(Span<byte> data, BlobVersionedHash container)
//    {

//        MemoryExtensions.CopyTo(container, data);



//    }

//    public static byte[] Encode(ICollection<BlobVersionedHash>? items)
//    {
//        if (items is null)
//        {
//            return [];
//        }
//        byte[] buf = new byte[GetLength(items)];
//        Encode(buf, items);
//        return buf;
//    }

//    public static void Encode(Span<byte> data, ICollection<BlobVersionedHash>? items)
//    {
//        if (items is null) return;

//        int offset = 0;
//        foreach (BlobVersionedHash item in items)
//        {
//            int length = GetLength(item);
//            Encode(data.Slice(offset, length), item);
//            offset += length;
//        }
//    }

//    public static void Decode(ReadOnlySpan<byte> data, out BlobVersionedHash container)
//    {
//        container = new();

//        data.Slice(0, 32).CopyTo(container);


//    }

//    public static void Decode(ReadOnlySpan<byte> data, out BlobVersionedHash[] container)
//    {
//        if (data.Length is 0)
//        {
//            container = [];
//            return;
//        }

//        int length = data.Length / 32;
//        container = new BlobVersionedHash[length];

//        int offset = 0;
//        for (int index = 0; index < length; index++)
//        {
//            Decode(data.Slice(offset, 32), out container[index]);
//            offset += 32;
//        }
//    }

//    public static void Merkleize(BlobVersionedHash container, out UInt256 root)
//    {
//        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(0));
//        merkleizer.CalculateRoot(out root);
//    }

//    public static void MerkleizeVector(IList<BlobVersionedHash>? container, out UInt256 root)
//    {
//        if (container is null)
//        {
//            root = 0;
//            return;
//        }

//        UInt256[] subRoots = new UInt256[container.Count];
//        for (int i = 0; i < container.Count; i++)
//        {
//            Merkleize(container[i], out subRoots[i]);
//        }

//        Merkle.Merkleize(out root, subRoots);
//    }

//    public static void MerkleizeList(IList<BlobVersionedHash>? container, ulong limit, out UInt256 root)
//    {
//        if (container is null || container.Count is 0)
//        {
//            root = 0;
//            Merkle.MixIn(ref root, (int)limit);
//            return;
//        }

//        MerkleizeVector(container, out root);
//        Merkle.MixIn(ref root, container.Count);
//    }
//}

//public partial class SszEncoding2
//{
//    public static int GetLength(Proof container)
//    {

//        return 48;
//    }

//    public static int GetLength(ICollection<Proof>? container)
//    {
//        if (container is null)
//        {
//            return 0;
//        }

//        return container.Count * 48;
//    }

//    public static byte[] Encode(Proof container)
//    {
//        byte[] buf = new byte[GetLength(container)];
//        Encode(buf, container);
//        return buf;
//    }

//    public static void Encode(Span<byte> data, Proof container)
//    {

//        MemoryExtensions.CopyTo(container, data);



//    }

//    public static byte[] Encode(ICollection<Proof>? items)
//    {
//        if (items is null)
//        {
//            return [];
//        }
//        byte[] buf = new byte[GetLength(items)];
//        Encode(buf, items);
//        return buf;
//    }

//    public static void Encode(Span<byte> data, ICollection<Proof>? items)
//    {
//        if (items is null) return;

//        int offset = 0;
//        foreach (Proof item in items)
//        {
//            int length = GetLength(item);
//            Encode(data.Slice(offset, length), item);
//            offset += length;
//        }
//    }

//    public static void Decode(ReadOnlySpan<byte> data, out Proof container)
//    {
//        container = new();

//        data.Slice(0, 48).CopyTo(container);


//    }

//    public static void Decode(ReadOnlySpan<byte> data, out Proof[] container)
//    {
//        if (data.Length is 0)
//        {
//            container = [];
//            return;
//        }

//        int length = data.Length / 48;
//        container = new Proof[length];

//        int offset = 0;
//        for (int index = 0; index < length; index++)
//        {
//            Decode(data.Slice(offset, 48), out container[index]);
//            offset += 48;
//        }
//    }

//    public static void Merkleize(Proof container, out UInt256 root)
//    {
//        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(0));
//        merkleizer.CalculateRoot(out root);
//    }

//    public static void MerkleizeVector(IList<Proof>? container, out UInt256 root)
//    {
//        if (container is null)
//        {
//            root = 0;
//            return;
//        }

//        UInt256[] subRoots = new UInt256[container.Count];
//        for (int i = 0; i < container.Count; i++)
//        {
//            Merkleize(container[i], out subRoots[i]);
//        }

//        Merkle.Merkleize(out root, subRoots);
//    }

//    public static void MerkleizeList(IList<Proof>? container, ulong limit, out UInt256 root)
//    {
//        if (container is null || container.Count is 0)
//        {
//            root = 0;
//            Merkle.MixIn(ref root, (int)limit);
//            return;
//        }

//        MerkleizeVector(container, out root);
//        Merkle.MixIn(ref root, container.Count);
//    }
//}


//public partial class SszEncoding2
//{
//    public static int GetLength(Blob container)
//    {

//        return 131072;
//    }

//    public static int GetLength(ICollection<Blob>? container)
//    {
//        if (container is null)
//        {
//            return 0;
//        }

//        return container.Count * 131072;
//    }

//    public static byte[] Encode(Blob container)
//    {
//        byte[] buf = new byte[GetLength(container)];
//        Encode(buf, container);
//        return buf;
//    }

//    public static void Encode(Span<byte> data, Blob container)
//    {

//        MemoryExtensions.CopyTo(container, data);



//    }

//    public static byte[] Encode(ICollection<Blob>? items)
//    {
//        if (items is null)
//        {
//            return [];
//        }
//        byte[] buf = new byte[GetLength(items)];
//        Encode(buf, items);
//        return buf;
//    }

//    public static void Encode(Span<byte> data, ICollection<Blob>? items)
//    {
//        if (items is null) return;

//        int offset = 0;
//        foreach (Blob item in items)
//        {
//            int length = GetLength(item);
//            Encode(data.Slice(offset, length), item);
//            offset += length;
//        }
//    }

//    public static void Decode(ReadOnlySpan<byte> data, out Blob container)
//    {
//        container = new();

//        data.Slice(0, 131072).CopyTo(container);


//    }

//    public static void Decode(ReadOnlySpan<byte> data, out Blob[] container)
//    {
//        if (data.Length is 0)
//        {
//            container = [];
//            return;
//        }

//        int length = data.Length / 131072;
//        container = new Blob[length];

//        int offset = 0;
//        for (int index = 0; index < length; index++)
//        {
//            Decode(data.Slice(offset, 131072), out container[index]);
//            offset += 131072;
//        }
//    }

//    public static void Merkleize(Blob? container, out UInt256 root)
//    {
//        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(0));
//        merkleizer.CalculateRoot(out root);
//    }

//    public static void MerkleizeVector(IList<Blob>? container, out UInt256 root)
//    {
//        if (container is null)
//        {
//            root = 0;
//            return;
//        }

//        UInt256[] subRoots = new UInt256[container.Count];
//        for (int i = 0; i < container.Count; i++)
//        {
//            Merkleize(container[i], out subRoots[i]);
//        }

//        Merkle.Merkleize(out root, subRoots);
//    }

//    public static void MerkleizeList(IList<Blob>? container, ulong limit, out UInt256 root)
//    {
//        if (container is null || container.Count is 0)
//        {
//            root = 0;
//            Merkle.MixIn(ref root, (int)limit);
//            return;
//        }

//        MerkleizeVector(container, out root);
//        Merkle.MixIn(ref root, container.Count);
//    }
//}
