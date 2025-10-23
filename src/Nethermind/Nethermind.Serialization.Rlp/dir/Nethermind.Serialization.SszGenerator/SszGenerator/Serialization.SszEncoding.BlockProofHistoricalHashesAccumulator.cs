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
    public static int GetLength(BlockProofHistoricalHashesAccumulator container)
    {

        return 480;
    }

    public static int GetLength(ICollection<BlockProofHistoricalHashesAccumulator>? container)
    {
        if(container is null)
        {
            return 0;
        }

        return container.Count * 480;
    }

    public static byte[] Encode(BlockProofHistoricalHashesAccumulator container)
    {
        byte[] buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }

    public static void Encode(Span<byte> data, BlockProofHistoricalHashesAccumulator container)
    {


        Encode(data.Slice(0, 480), container.Data);

    }

    public static byte[] Encode(ICollection<BlockProofHistoricalHashesAccumulator>? items)
    {
        if (items is null)
        {
            return [];
        }
        byte[] buf = new byte[GetLength(items)];
        Encode(buf, items);
        return buf;
    }

    public static void Encode(Span<byte> data, ICollection<BlockProofHistoricalHashesAccumulator>? items)
    {
        if(items is null) return;

        int offset = 0;
        foreach(BlockProofHistoricalHashesAccumulator item in items)
        {
            int length = GetLength(item);
            Encode(data.Slice(offset, length), item);
            offset += length;
        }
    }

    public static void Decode(ReadOnlySpan<byte> data, out BlockProofHistoricalHashesAccumulator container)
    {
        container = new();

        Decode(data.Slice(0, 480), out SSZBytes32[] _data); container.Data = [ .._data];

    }

    public static void Decode(ReadOnlySpan<byte> data, out BlockProofHistoricalHashesAccumulator[] container)
    {
        if(data.Length is 0)
        {
            container = [];
            return;
        }

        int length = data.Length / 480;
        container = new BlockProofHistoricalHashesAccumulator[length];

        int offset = 0;
        for(int index = 0; index < length; index++)
        {
            Decode(data.Slice(offset, 480), out container[index]);
            offset += 480;
        }
    }

    public static void Merkleize(BlockProofHistoricalHashesAccumulator container, out UInt256 root)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(1));
        MerkleizeVector(container.Data, out UInt256 _dataRoot); merkleizer.Feed(_dataRoot);
        merkleizer.CalculateRoot(out root);
    }

    public static void MerkleizeVector(IList<BlockProofHistoricalHashesAccumulator>? container, out UInt256 root)
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

    public static void MerkleizeList(IList<BlockProofHistoricalHashesAccumulator>? container, ulong limit, out UInt256 root)
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