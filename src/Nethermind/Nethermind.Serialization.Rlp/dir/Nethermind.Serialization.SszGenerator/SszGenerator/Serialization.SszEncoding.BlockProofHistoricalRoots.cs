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
    public static int GetLength(BlockProofHistoricalRoots container)
    {

        return 840;
    }

    public static int GetLength(ICollection<BlockProofHistoricalRoots>? container)
    {
        if(container is null)
        {
            return 0;
        }

        return container.Count * 840;
    }

    public static byte[] Encode(BlockProofHistoricalRoots container)
    {
        byte[] buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }

    public static void Encode(Span<byte> data, BlockProofHistoricalRoots container)
    {


        Encode(data.Slice(0, 448), container.BeaconBlockProofData);
        Encode(data.Slice(448, 32), container.BeaconBlockRootData);
        Encode(data.Slice(480, 352), container.ExecutionBlockProofData);
        SszLib.Encode(data.Slice(832, 8), container.Slot);

    }

    public static byte[] Encode(ICollection<BlockProofHistoricalRoots>? items)
    {
        if (items is null)
        {
            return [];
        }
        byte[] buf = new byte[GetLength(items)];
        Encode(buf, items);
        return buf;
    }

    public static void Encode(Span<byte> data, ICollection<BlockProofHistoricalRoots>? items)
    {
        if(items is null) return;

        int offset = 0;
        foreach(BlockProofHistoricalRoots item in items)
        {
            int length = GetLength(item);
            Encode(data.Slice(offset, length), item);
            offset += length;
        }
    }

    public static void Decode(ReadOnlySpan<byte> data, out BlockProofHistoricalRoots container)
    {
        container = new();

        Decode(data.Slice(0, 448), out SSZBytes32[] beaconBlockProofData); container.BeaconBlockProofData = [ ..beaconBlockProofData];
        Decode(data.Slice(448, 32), out SSZBytes32 beaconBlockRootData); container.BeaconBlockRootData = beaconBlockRootData;
        Decode(data.Slice(480, 352), out SSZBytes32[] executionBlockProofData); container.ExecutionBlockProofData = [ ..executionBlockProofData];
        SszLib.Decode(data.Slice(832, 8), out Int64 slot); container.Slot = slot;

    }

    public static void Decode(ReadOnlySpan<byte> data, out BlockProofHistoricalRoots[] container)
    {
        if(data.Length is 0)
        {
            container = [];
            return;
        }

        int length = data.Length / 840;
        container = new BlockProofHistoricalRoots[length];

        int offset = 0;
        for(int index = 0; index < length; index++)
        {
            Decode(data.Slice(offset, 840), out container[index]);
            offset += 840;
        }
    }

    public static void Merkleize(BlockProofHistoricalRoots container, out UInt256 root)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(4));
        MerkleizeVector(container.BeaconBlockProofData, out UInt256 beaconBlockProofDataRoot); merkleizer.Feed(beaconBlockProofDataRoot);
        Merkleize(container.BeaconBlockRootData, out UInt256 beaconBlockRootDataRoot); merkleizer.Feed(beaconBlockRootDataRoot);
        MerkleizeVector(container.ExecutionBlockProofData, out UInt256 executionBlockProofDataRoot); merkleizer.Feed(executionBlockProofDataRoot);
        merkleizer.Feed(container.Slot);
        merkleizer.CalculateRoot(out root);
    }

    public static void MerkleizeVector(IList<BlockProofHistoricalRoots>? container, out UInt256 root)
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

    public static void MerkleizeList(IList<BlockProofHistoricalRoots>? container, ulong limit, out UInt256 root)
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