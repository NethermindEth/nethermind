// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>MSB-first bit-string helpers used by canonical compressed branches.</summary>
public static class PbtBitPrefix
{
    public const int MaxBitCount = ushort.MaxValue;

    internal static int ByteCount(int bitCount) => (bitCount + 7) >> 3;

    /// <summary>Copies an MSB-first bit range into a zeroed destination range, preserving adjacent bits.</summary>
    internal static void CopyBits(ReadOnlySpan<byte> source, int sourceOffset, int bitCount, Span<byte> destination, int destinationOffset)
    {
        int sourceShift = sourceOffset & 7;
        int destinationShift = destinationOffset & 7;
        int sourceByte = sourceOffset >> 3;
        int destinationByte = destinationOffset >> 3;
        if (sourceShift == 0 && destinationShift == 0)
        {
            int wholeBytes = bitCount >> 3;
            source.Slice(sourceByte, wholeBytes).CopyTo(destination[destinationByte..]);
            sourceByte += wholeBytes;
            destinationByte += wholeBytes;
            bitCount &= 7;
        }

        for (; bitCount > 0; bitCount -= 8, sourceByte++, destinationByte++)
        {
            int count = Math.Min(8, bitCount);
            int value = (source[sourceByte] << sourceShift) & 0xFF;
            if (sourceShift + count > 8) value |= source[sourceByte + 1] >> (8 - sourceShift);
            value &= 0xFF << (8 - count);
            destination[destinationByte] |= (byte)(value >> destinationShift);
            if (destinationShift + count > 8)
                destination[destinationByte + 1] |= (byte)(value << (8 - destinationShift));
        }
    }
}
