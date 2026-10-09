// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Pbt.Common;

/// <summary>
/// Decodes the persisted <see cref="PbtColumns.Storages"/> row of one <see cref="PackedSlotRun"/>:
/// <c>[type][mask u16 LE][32-byte value × popcount(mask), ascending slot]</c>, where the type byte is the
/// log2 of the capacity that wrote it (see <see cref="PackedSlotRun.Encode"/>). An empty run is a row deletion and is never encoded.
/// </summary>
internal static class SlotRunCodec
{
    internal const int HeaderLength = 1 + sizeof(ushort);
    private const byte MaxType = 4;

    /// <summary>Whether <paramref name="length"/> is the length of an encoded run holding at least one slot.</summary>
    public static bool IsValidEncodedLength(int length) =>
        length is >= HeaderLength + ValueHash256.MemorySize and <= HeaderLength + SlotRun.Width * ValueHash256.MemorySize
        && (length - HeaderLength) % ValueHash256.MemorySize == 0;

    public static PackedSlotRun Decode(ReadOnlySpan<byte> encoded) => SlotRun.CreatePacked(ReadMask(encoded), encoded[HeaderLength..]);

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
