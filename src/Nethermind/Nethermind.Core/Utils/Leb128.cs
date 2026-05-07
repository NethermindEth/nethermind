// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Utils;

/// <summary>
/// LEB128 variable-length integer encoding/decoding.
/// </summary>
public static class Leb128
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Read(ReadOnlySpan<byte> data, ref int offset)
    {
        long result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = data[offset++];
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(Span<byte> data, int offset, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            data[offset++] = (byte)(v | 0x80);
            v >>= 7;
        }
        data[offset++] = (byte)v;
        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(Span<byte> data, int offset, long value)
    {
        ulong v = (ulong)value;
        while (v >= 0x80)
        {
            data[offset++] = (byte)(v | 0x80);
            v >>= 7;
        }
        data[offset++] = (byte)v;
        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int EncodedSize(int value)
    {
        uint v = (uint)value;
        int size = 0;
        do
        {
            size++;
            v >>= 7;
        }
        while (v != 0);
        return size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int EncodedSize(long value)
    {
        ulong v = (ulong)value;
        int size = 0;
        do
        {
            size++;
            v >>= 7;
        }
        while (v != 0);
        return size;
    }
}
