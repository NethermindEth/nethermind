// Copyright 2022 Demerzel Solutions Limited
// Licensed under Apache-2.0.For full terms, see LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Nethermind.Verkle.Fields;

[StructLayout(LayoutKind.Explicit)]
public struct U3
{
    /* in little endian order so u3 is the most significant ulong */
    [FieldOffset(0)] public ulong u0;
    [FieldOffset(8)] public ulong u1;
    [FieldOffset(16)] public ulong u2;
}

[StructLayout(LayoutKind.Explicit)]
public struct U4
{
    /* in little endian order so u3 is the most significant ulong */
    [FieldOffset(0)] public ulong u0;
    [FieldOffset(8)] public ulong u1;
    [FieldOffset(16)] public ulong u2;
    [FieldOffset(24)] public ulong u3;
}

[StructLayout(LayoutKind.Explicit)]
public struct U7
{
    /* in little endian order so u3 is the most significant ulong */
    [FieldOffset(0)] public ulong u0;
    [FieldOffset(8)] public ulong u1;
    [FieldOffset(16)] public ulong u2;
    [FieldOffset(24)] public ulong u3;
    [FieldOffset(32)] public ulong u4;
    [FieldOffset(40)] public ulong u5;
    [FieldOffset(48)] public ulong u6;
}

public static class FieldUtils
{
    public static void Create(in ReadOnlySpan<byte> bytes, out ulong u0, out ulong u1, out ulong u2, out ulong u3)
    {
        int byteCount = bytes.Length;
        int unalignedBytes = byteCount % 8;
        int dwordCount = (byteCount / 8) + (unalignedBytes == 0 ? 0 : 1);

        ulong cs0 = 0;
        ulong cs1 = 0;
        ulong cs2 = 0;
        ulong cs3 = 0;

        if (dwordCount == 0)
        {
            u0 = u1 = u2 = u3 = 0;
            return;
        }

        if (dwordCount >= 1)
        {
            for (int j = 8; j > 0; j--)
            {
                cs0 <<= 8;
                if (j <= byteCount) cs0 |= bytes[byteCount - j];
            }
        }

        if (dwordCount >= 2)
        {
            for (int j = 16; j > 8; j--)
            {
                cs1 <<= 8;
                if (j <= byteCount) cs1 |= bytes[byteCount - j];
            }
        }

        if (dwordCount >= 3)
        {
            for (int j = 24; j > 16; j--)
            {
                cs2 <<= 8;
                if (j <= byteCount) cs2 |= bytes[byteCount - j];
            }
        }

        if (dwordCount >= 4)
        {
            for (int j = 32; j > 24; j--)
            {
                cs3 <<= 8;
                if (j <= byteCount) cs3 |= bytes[byteCount - j];
            }
        }

        u0 = cs0;
        u1 = cs1;
        u2 = cs2;
        u3 = cs3;
    }
}
