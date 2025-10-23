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
    public static int GetLength(BlockProofHistoricalSummaries container)
    {

        return 460
                + GetLength(container.ExecutionBlockProofData);
    }

    public static int GetLength(ICollection<BlockProofHistoricalSummaries>? container)
    {
        if(container is null)
        {
            return 0;
        }

        int length = container.Count * 4;
        foreach(BlockProofHistoricalSummaries item in container)
        {
            length += GetLength(item);
        }

        return length;
    }

    public static byte[] Encode(BlockProofHistoricalSummaries container)
    {
        byte[] buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }

    public static void Encode(Span<byte> data, BlockProofHistoricalSummaries container)
    {

        int offset1 = 460;

        Encode(data.Slice(0, 416), container.BeaconBlockProofData);
        Encode(data.Slice(416, 32), container.BeaconBlockRootData);
        SszLib.Encode(data.Slice(448, 4), offset1);
        SszLib.Encode(data.Slice(452, 8), container.Slot);

        Encode(data.Slice(offset1, data.Length - offset1), container.ExecutionBlockProofData);
    }

    public static byte[] Encode(ICollection<BlockProofHistoricalSummaries>? items)
    {
        if (items is null)
        {
            return [];
        }
        byte[] buf = new byte[GetLength(items)];
        Encode(buf, items);
        return buf;
    }

    public static void Encode(Span<byte> data, ICollection<BlockProofHistoricalSummaries>? items)
    {
        if(items is null) return;

        int offset = items.Count * 4;
        int itemOffset = 0;

        foreach(BlockProofHistoricalSummaries item in items)
        {
            SszLib.Encode(data.Slice(itemOffset, 4), offset);
            itemOffset += 4;
            int length = GetLength(item);
            Encode(data.Slice(offset, length), item);
            offset += length;
        }
    }

    public static void Decode(ReadOnlySpan<byte> data, out BlockProofHistoricalSummaries container)
    {
        container = new();

        Decode(data.Slice(0, 416), out SSZBytes32[] beaconBlockProofData); container.BeaconBlockProofData = [ ..beaconBlockProofData];
        Decode(data.Slice(416, 32), out SSZBytes32 beaconBlockRootData); container.BeaconBlockRootData = beaconBlockRootData;
        SszLib.Decode(data.Slice(448, 4), out int offset1);
        SszLib.Decode(data.Slice(452, 8), out Int64 slot); container.Slot = slot;

        if (data.Length - offset1 > 0) { Decode(data.Slice(offset1, data.Length - offset1), out SSZBytes32[] executionBlockProofData); container.ExecutionBlockProofData = [ ..executionBlockProofData]; }
    }

    public static void Decode(ReadOnlySpan<byte> data, out BlockProofHistoricalSummaries[] container)
    {
        if(data.Length is 0)
        {
            container = [];
            return;
        }

        SszLib.Decode(data.Slice(0, 4), out int firstOffset);
        int length = firstOffset / 4;
        container = new BlockProofHistoricalSummaries[length];

        int index = 0;
        int offset = firstOffset;
        for(int nextOffsetIndex = 4; index < length - 1; index++, nextOffsetIndex += 4)
        {
            SszLib.Decode(data.Slice(nextOffsetIndex, 4), out int nextOffset);
            Decode(data.Slice(offset, nextOffset - offset), out container[index]);
            offset = nextOffset;
        }

        Decode(data.Slice(offset), out container[index]);
    }

    public static void Merkleize(BlockProofHistoricalSummaries container, out UInt256 root)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(4));
        MerkleizeVector(container.BeaconBlockProofData, out UInt256 beaconBlockProofDataRoot); merkleizer.Feed(beaconBlockProofDataRoot);
        Merkleize(container.BeaconBlockRootData, out UInt256 beaconBlockRootDataRoot); merkleizer.Feed(beaconBlockRootDataRoot);
        MerkleizeList(container.ExecutionBlockProofData, 12, out UInt256 executionBlockProofDataRoot); merkleizer.Feed(executionBlockProofDataRoot);
        merkleizer.Feed(container.Slot);
        merkleizer.CalculateRoot(out root);
    }

    public static void MerkleizeVector(IList<BlockProofHistoricalSummaries>? container, out UInt256 root)
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

    public static void MerkleizeList(IList<BlockProofHistoricalSummaries>? container, ulong limit, out UInt256 root)
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