// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Logs;

public static class BinaryEncoding
{
    public static int TryReadVarInt(ReadOnlySpan<byte> span, int offset, out uint value)
    {
        if ((uint)offset >= (uint)span.Length)
        {
            value = 0;
            return 0;
        }

        const int bits = 7;

        value = span[offset++];
        if ((value & 0x80) == 0) return 1;
        value &= 0x7F;

        if ((uint)offset >= (uint)span.Length) return -1;
        uint chunk = span[offset++];
        value |= (chunk & 0x7F) << (1 * bits);
        if ((chunk & 0x80) == 0) return 2;

        if ((uint)offset >= (uint)span.Length) return -1;
        chunk = span[offset++];
        value |= (chunk & 0x7F) << (2 * bits);
        if ((chunk & 0x80) == 0) return 3;

        if ((uint)offset >= (uint)span.Length) return -1;
        chunk = span[offset++];
        value |= (chunk & 0x7F) << (3 * bits);
        if ((chunk & 0x80) == 0) return 4;

        // Use 32 - 28 bits as the last one
        value |= (chunk & 0b00001111) << (4 * bits);
        return MaxVarIntByteCount;
    }

    public const int MaxVarIntByteCount = 5;

    public static int WriteVarInt(uint value, Span<byte> span, int offset = 0)
    {
        int count = 0;
        do
        {
            span[offset++] = (byte)((value & 0x7F) | 0x80);
            count++;
        } while ((value >>= 7) != 0);

        span[offset - 1] &= 0x7F;
        return count;
    }
}
