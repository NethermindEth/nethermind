// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>
/// The persisted <see cref="PbtColumns.Storages"/> row of one <see cref="ISlotRun"/>:
/// <c>[type][mask u16 LE][32-byte value × popcount(mask), ascending slot]</c>, where the type byte is the
/// log2 of the capacity that wrote it. An empty run is a row deletion and is never encoded.
/// </summary>
internal static class SlotRunCodec
{
    private const int HeaderLength = 1 + sizeof(ushort);
    private const byte MaxType = 4;
    public const int MaxEncodedLength = HeaderLength + SlotRun.Width * ValueHash256.MemorySize;

    public static int Encode(ISlotRun run, Span<byte> destination)
    {
        if (run.Count == 0) throw new ArgumentException("An empty run is persisted as a row deletion.", nameof(run));
        PackedSlotRun packed = (PackedSlotRun)run;
        destination[0] = (byte)BitOperations.Log2((uint)packed.Capacity);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..], packed.Mask);
        ReadOnlySpan<byte> values = MemoryMarshal.AsBytes(packed.PackedValues);
        values.CopyTo(destination[HeaderLength..]);
        return HeaderLength + values.Length;
    }

    public static ISlotRun Decode(ReadOnlySpan<byte> encoded)
    {
        ushort mask = ReadMask(encoded);
        Span<EvmWord> valuesByIndex = stackalloc EvmWord[SlotRun.Width];
        int rank = 0;
        for (int index = 0; index < SlotRun.Width; index++)
            if ((mask & (1 << index)) != 0) valuesByIndex[index] = ValueAt(encoded, rank++);
        return SlotRun.Create(mask, valuesByIndex);
    }

    private static EvmWord ValueAt(ReadOnlySpan<byte> encoded, int rank) =>
        EvmWordSlot.FromStripped(encoded.Slice(HeaderLength + rank * ValueHash256.MemorySize, ValueHash256.MemorySize));

    private static ushort ReadMask(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < HeaderLength || encoded[0] > MaxType) throw new InvalidDataException("Invalid persisted PBT slot run header.");
        ushort mask = BinaryPrimitives.ReadUInt16LittleEndian(encoded[1..]);
        int count = BitOperations.PopCount(mask);
        if (count == 0 || count > 1 << encoded[0] || encoded.Length != HeaderLength + count * ValueHash256.MemorySize)
            throw new InvalidDataException("Invalid persisted PBT slot run length.");
        return mask;
    }
}
