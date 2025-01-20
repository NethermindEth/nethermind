// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Logs;

public static class PrimitiveExtensions
{
    public static void WriteNativeEndian(this IBufferWriter<byte> w, int deduplicatedLength)
    {
        w.Write(MemoryMarshal.Cast<int, byte>(MemoryMarshal.CreateSpan(ref deduplicatedLength, 1)));
    }

    public static void WriteNativeEndianSpan<T>(this IBufferWriter<byte> w, ReadOnlySpan<T> data)
        where T : struct
    {
        w.Write(MemoryMarshal.Cast<T, byte>(data));
    }

    public static int ReadNativeEndian(this ReadOnlySpan<byte> data) => Unsafe.ReadUnaligned<int>(ref MemoryMarshal.GetReference(data));
}
