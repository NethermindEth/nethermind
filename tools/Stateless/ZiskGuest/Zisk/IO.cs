// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Stateless.ZiskGuest.Zisk;

public static unsafe class IO
{
    private const ulong INPUT_ADDR = 0x90000000UL;
    private const ulong OUTPUT_ADDR = 0xa0010000UL;
    private const ulong UART_ADDR = 0xa0000200UL;

    public static ReadOnlySpan<byte> ReadInput()
    {
        ulong size = *(ulong*)(INPUT_ADDR + 8);

        if (size > int.MaxValue)
            Environment.FailFast("Input size exceeds the maximum supported length");

        return new ReadOnlySpan<byte>((void*)(INPUT_ADDR + 16), (int)size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOutput(int id, uint value)
    {
        if ((uint)id >= 64U)
            return;

        uint* baseAddr = (uint*)OUTPUT_ADDR;
        uint outputIndex = (uint)id + 1U;

        baseAddr[outputIndex] = value;

        uint currentCount = baseAddr[0];

        if (currentCount < outputIndex)
            baseAddr[0] = outputIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(char value) => *(byte*)UART_ADDR = unchecked((byte)value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(string value)
    {
        byte* uart = (byte*)UART_ADDR;

        for (int i = 0; i < value.Length; i++)
            *uart = unchecked((byte)value[i]);
    }

    public static void WriteLine(string value)
    {
        Write(value);
        Write('\n');
    }
}
