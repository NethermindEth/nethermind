// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Portal;

/// <summary>
/// Copied from https://github.com/rzubek/mini-leb128/blob/master/LEB128.cs
/// TODO: Double check if Nuget has this
/// Single-file utility to read and write integers in the LEB128 (7-bit little endian base-128) format.
/// See https://en.wikipedia.org/wiki/LEB128 for details.
/// </summary>
public static class LEB128
{
    private const long SIGN_EXTEND_MASK = -1L;
    private const int INT64_BITSIZE = sizeof(long) * 8;

    public static void WriteLEB128Signed(this Stream stream, long value) => stream.WriteLEB128Signed(value, out _);

    public static void WriteLEB128Signed(this Stream stream, long value, out int bytes)
    {
        bytes = 0;
        var more = true;

        while (more)
        {
            var chunk = (byte)(value & 0x7fL); // extract a 7-bit chunk
            value >>= 7;

            var signBitSet = (chunk & 0x40) != 0; // sign bit is the msb of a 7-bit byte, so 0x40
            more = !(value == 0 && !signBitSet || value == -1 && signBitSet);
            if (more) chunk |= 0x80;
            stream.WriteByte(chunk);
            bytes += 1;
        };
    }

    public static void WriteLEB128Unsigned(this Stream stream, ulong value) => stream.WriteLEB128Unsigned(value, out _);

    public static void WriteLEB128Unsigned(this Stream stream, ulong value, out int bytes)
    {
        bytes = 0;
        var more = true;

        while (more)
        {
            var chunk = (byte)(value & 0x7fUL); // extract a 7-bit chunk
            value >>= 7;

            more = value != 0;
            if (more) chunk |= 0x80;
            stream.WriteByte(chunk);
            bytes += 1;
        };
    }

    public static long ReadLEB128Signed(this Stream stream) => stream.ReadLEB128Signed(out _);

    public static long ReadLEB128Signed(this Stream stream, out int bytes)
    {
        bytes = 0;

        long value = 0;
        var shift = 0;
        bool more = true, signBitSet = false;

        while (more)
        {
            var next = stream.ReadByte();
            if (next < 0) throw new InvalidOperationException("Unexpected end of stream");
            var b = (byte)next;
            bytes += 1;

            more = (b & 0x80) != 0; // extract msb
            signBitSet = (b & 0x40) != 0; // sign bit is the msb of a 7-bit byte, so 0x40

            var chunk = b & 0x7fL; // extract lower 7 bits
            value |= chunk << shift;
            shift += 7;
        };

        // extend the sign of shorter negative numbers
        if (shift < INT64_BITSIZE && signBitSet) value |= SIGN_EXTEND_MASK << shift;
        return value;
    }

    public static ulong ReadLEB128Unsigned(this Stream stream) => stream.ReadLEB128Unsigned(out _);

    public static ulong ReadLEB128Unsigned(this Stream stream, out int bytes)
    {
        bytes = 0;

        ulong value = 0;
        var shift = 0;
        var more = true;

        while (more)
        {
            var next = stream.ReadByte();
            if (next < 0) throw new InvalidOperationException("Unexpected end of stream");
            var b = (byte)next;
            bytes += 1;

            more = (b & 0x80) != 0;   // extract msb
            var chunk = b & 0x7fUL; // extract lower 7 bits
            value |= chunk << shift;
            shift += 7;
        }

        return value;
    }

}
