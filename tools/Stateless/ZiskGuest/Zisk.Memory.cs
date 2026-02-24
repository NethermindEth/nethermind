// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Stateless.ZiskGuest;

public static unsafe partial class Zisk
{
    private const ulong INPUT_ADDR = 0x90000000UL;
    private const ulong OUTPUT_ADDR = 0xa0010000UL;
    private const ulong UART_ADDR = 0xa0000200UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] ReadInput()
    {
        byte* sizePtr = (byte*)(INPUT_ADDR + 8);
        ulong size = 0;

        for (int i = 0; i < 8; i++)
            size |= ((ulong)sizePtr[i]) << (i * 8);

        if (size == 0) return [];

        byte[] result = new byte[size];
        byte* dataPtr = (byte*)(INPUT_ADDR + 16);

        for (ulong i = 0; i < size; i++)
            result[i] = dataPtr[i];

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOutput(int id, uint value)
    {
        if (id < 0 || id >= 64) return;

        uint* baseAddr = (uint*)OUTPUT_ADDR;
        uint currentCount = *baseAddr;

        if (id + 1 > currentCount)
            *baseAddr = (uint)(id + 1);

        *(baseAddr + 1 + id) = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteChar(char c)
    {
        *(byte*)UART_ADDR = (byte)c;
    }

    public static void WriteString(string s)
    {
        for (int i = 0; i < s.Length; i++)
            WriteChar(s[i]);
    }

    public static void WriteLine(string s)
    {
        WriteString(s);
        WriteChar('\n');
    }

    /// Read 64-bit unsigned integer from byte array (little-endian)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadUInt64(byte[] data, int offset = 0)
    {
        if (data.Length < offset + 8) return 0;

        ulong result = 0;
        for (int i = 0; i < 8; i++)
            result |= ((ulong)data[offset + i]) << (i * 8);
        return result;
    }

    /// Read 32-bit unsigned integer from byte array (little-endian)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUInt32(byte[] data, int offset = 0)
    {
        if (data.Length < offset + 4) return 0;

        uint result = 0;
        for (int i = 0; i < 4; i++)
            result |= ((uint)data[offset + i]) << (i * 8);
        return result;
    }

    /// Output 64-bit value as two 32-bit outputs
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOutput64(int baseId, ulong value)
    {
        SetOutput(baseId, (uint)(value & 0xFFFFFFFF));
        SetOutput(baseId + 1, (uint)(value >> 32));
    }
}
