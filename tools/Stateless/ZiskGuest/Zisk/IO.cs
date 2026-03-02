// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Stateless.ZiskGuest.Zisk;

public static unsafe class IO
{
    private static readonly byte* Input = (byte*)0x9000_0000UL; // INPUT_ADDR
    private static readonly uint* Output = (uint*)0xa001_0000UL; // OUTPUT_ADDR
    private static readonly byte* Uart = (byte*)0xa000_0200UL; // UART_ADDR

    public static ReadOnlySpan<byte> ReadInput()
    {
        ulong size = *(ulong*)(Input + sizeof(ulong));

        if (size > int.MaxValue)
            Environment.FailFast("Input size exceeds the maximum supported length");

        return new ReadOnlySpan<byte>(Input + 2 * sizeof(ulong), (int)size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetOutput(int id, uint value)
    {
        if ((uint)id >= 64U)
            Environment.FailFast("Output id must be between 0 and 63");

        uint index = (uint)id + 1U;

        Output[index] = value;

        uint count = Output[0];

        if (count < index)
            Output[0] = index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(char value) => *Uart = unchecked((byte)value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(string value)
    {
        for (int i = 0; i < value.Length; i++)
            *Uart = unchecked((byte)value[i]);
    }

    public static void WriteLine(string value)
    {
        Write(value);
        Write('\n');
    }
}
