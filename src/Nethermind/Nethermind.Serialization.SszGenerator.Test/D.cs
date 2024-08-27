using Nethermind.Int256;
using Nethermind.Merkleization;
using System;
using System.Collections.Generic;
using System.Linq;

using SszLib = Nethermind.Serialization.Ssz.Ssz;

namespace Nethermind.Serialization.SszGenerator.Test.Serialization;

public partial class SszEncoding2
{
    public static int GetLength(SlotDecryptionIdentites container)
    {
        return 36 +
               GetLength(container.IdentityPreimages);
    }

    public static int GetLength(ICollection<SlotDecryptionIdentites> container)
    {
        int length = container.Count * 4;
        foreach (SlotDecryptionIdentites item in container)
        {
            length += GetLength(item);
        }
        return length;
    }

    public static ReadOnlySpan<byte> Encode(SlotDecryptionIdentites container)
    {
        Span<byte> buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }

    public static void Encode(Span<byte> buf, SlotDecryptionIdentites container)
    {
        int dynOffset1 = 36;


        SszLib.Encode(buf.Slice(0, 8), container.InstanceID);
        SszLib.Encode(buf.Slice(8, 8), container.Eon);
        SszLib.Encode(buf.Slice(16, 8), container.Slot);
        SszLib.Encode(buf.Slice(24, 8), container.TxPointer);
        SszLib.Encode(buf.Slice(32, 4), dynOffset1);

        if (container.IdentityPreimages is not null) Encode(buf.Slice(dynOffset1, GetLength(container.IdentityPreimages)), container.IdentityPreimages);


    }

    public static void Encode(Span<byte> buf, ICollection<SlotDecryptionIdentites> container)
    {
        int offset = container.Count * 4;
        int itemOffset = 0;
        foreach (SlotDecryptionIdentites item in container)
        {
            SszLib.Encode(buf.Slice(itemOffset, 4), offset);
            itemOffset += 4;
            int length = GetLength(item);
            Encode(buf.Slice(offset, length), item);
            offset += length;
        }
    }

    public static void Decode(ReadOnlySpan<byte> data, out SlotDecryptionIdentites container)
    {
        container = new();
        int dynOffset1 = 36;

        container.InstanceID = SszLib.DecodeULong(data.Slice(0, 8));
        container.Eon = SszLib.DecodeULong(data.Slice(8, 8));
        container.Slot = SszLib.DecodeULong(data.Slice(16, 8));
        container.TxPointer = SszLib.DecodeULong(data.Slice(24, 8));
        int dynOffset2 = SszLib.DecodeInt(data.Slice(32, 4));
        if (data.Length - dynOffset1 > 0) { Decode(data.Slice(dynOffset1, data.Length - dynOffset1), out List<Nethermind.Serialization.SszGenerator.Test.IdentityPreimage> value); container.IdentityPreimages = value; };


    }

    public static void Decode(ReadOnlySpan<byte> data, out SlotDecryptionIdentites[] container)
    {
        if (data.Length is 0)
        {
            container = [];
            return;
        }

        int firstOffset = SszLib.DecodeInt(data.Slice(0, 4));
        int length = firstOffset / 4;

        container = new SlotDecryptionIdentites[length];

        int index = 0;
        int offset = firstOffset;
        for (int nextOffsetIndex = 4; index < length - 1; index++, nextOffsetIndex += 4)
        {
            int nextOffset = SszLib.DecodeInt(data.Slice(nextOffsetIndex, 4));
            Decode(data.Slice(offset, nextOffset - offset), out container[index]);
            offset = nextOffset;
        }
        Decode(data.Slice(offset, length), out container[index]);
    }

    public static void Decode(ReadOnlySpan<byte> data, out List<SlotDecryptionIdentites> container)
    {
        Decode(data, out SlotDecryptionIdentites[] array);
        container = array.ToList();
    }

    public static void Merkleize(SlotDecryptionIdentites container, out UInt256 root)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(5));

        merkleizer.Feed(container.InstanceID);
        merkleizer.Feed(container.Eon);
        merkleizer.Feed(container.Slot);
        merkleizer.Feed(container.TxPointer);
        Merkleize(container.IdentityPreimages, out UInt256 rootOfIdentityPreimages);
        merkleizer.Feed(rootOfIdentityPreimages);

        merkleizer.CalculateRoot(out root);
    }

    public static void Merkleize(ICollection<SlotDecryptionIdentites> container, out UInt256 root)
    {
        Merkleize(container, (ulong)container.Count, out root);
    }

    public static void Merkleize(ICollection<SlotDecryptionIdentites> container, ulong limit, out UInt256 root)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(limit));

        foreach (SlotDecryptionIdentites item in container)
        {
            Merkleize(item, out UInt256 localRoot);
            merkleizer.Feed(localRoot);
        }

        merkleizer.CalculateRoot(out root);
    }


    public static int GetLength(IdentityPreimage container)
    {
        return 4 +
               (container.Data?.Length ?? 0);
    }

    public static int GetLength(ICollection<IdentityPreimage> container)
    {
        int length = container.Count * 4;
        foreach (IdentityPreimage item in container)
        {
            length += GetLength(item);
        }
        return length;
    }

    public static ReadOnlySpan<byte> Encode(IdentityPreimage container)
    {
        Span<byte> buf = new byte[GetLength(container)];
        Encode(buf, container);
        return buf;
    }

    public static void Encode(Span<byte> buf, IdentityPreimage container)
    {
        int dynOffset1 = 4;


        SszLib.Encode(buf.Slice(0, 4), dynOffset1);

        if (container.Data is not null) SszLib.Encode(buf.Slice(dynOffset1, (container.Data?.Length ?? 0)), container.Data);


    }

    public static void Encode(Span<byte> buf, ICollection<IdentityPreimage> container)
    {
        int offset = container.Count * 4;
        int itemOffset = 0;
        foreach (IdentityPreimage item in container)
        {
            SszLib.Encode(buf.Slice(itemOffset, 4), offset);
            itemOffset += 4;
            int length = GetLength(item);
            Encode(buf.Slice(offset, length), item);
            offset += length;
        }
    }

    public static void Decode(ReadOnlySpan<byte> data, out IdentityPreimage container)
    {
        container = new();
        int dynOffset1 = 4;

        int dynOffset2 = SszLib.DecodeInt(data.Slice(0, 4));
        if (data.Length - dynOffset1 > 0) { SszLib.Decode(data.Slice(dynOffset1, data.Length - dynOffset1), out byte[] value); container.Data = value; };
    }

    public static void Decode(ReadOnlySpan<byte> data, out IdentityPreimage[] container)
    {
        if (data.Length is 0)
        {
            container = [];
            return;
        }

        int firstOffset = SszLib.DecodeInt(data.Slice(0, 4));
        int length = firstOffset / 4;

        container = new IdentityPreimage[length];

        int index = 0;
        int offset = firstOffset;
        for (int nextOffsetIndex = 4; index < length - 1; index++, nextOffsetIndex += 4)
        {
            int nextOffset = SszLib.DecodeInt(data.Slice(nextOffsetIndex, 4));
            Decode(data.Slice(offset, nextOffset - offset), out container[index]);
            offset = nextOffset;
        }
        Decode(data.Slice(offset, length), out container[index]);
    }

    public static void Decode(ReadOnlySpan<byte> data, out List<IdentityPreimage> container)
    {
        Decode(data, out IdentityPreimage[] array);
        container = array.ToList();
    }

    public static void Merkleize(IdentityPreimage container, out UInt256 root)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(1));

        Merkleize(container.Data, out UInt256 rootOfData);
        merkleizer.Feed(rootOfData);

        merkleizer.CalculateRoot(out root);
    }

    public static void Merkleize(ICollection<IdentityPreimage> container, out UInt256 root)
    {
        Merkleize(container, (ulong)container.Count, out root);
    }

    public static void Merkleize(ICollection<IdentityPreimage> container, ulong limit, out UInt256 root)
    {
        Merkleizer merkleizer = new Merkleizer(Merkle.NextPowerOfTwoExponent(limit));

        foreach (IdentityPreimage item in container)
        {
            Merkleize(item, out UInt256 localRoot);
            merkleizer.Feed(localRoot);
        }

        merkleizer.CalculateRoot(out root);
    }
}
