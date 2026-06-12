// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Int256;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial UInt256 ReadBeWord(ref byte bytes)
    {
        ulong u3, u2, u1, u0;
        if (BitConverter.IsLittleEndian)
        {
            // movbe reg, mem (load + endianness flip in one).
            u3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref bytes));
            u2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, sizeof(ulong))));
            u1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 2 * sizeof(ulong))));
            u0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 3 * sizeof(ulong))));
        }
        else
        {
            u3 = Unsafe.ReadUnaligned<ulong>(ref bytes);
            u2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, sizeof(ulong)));
            u1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 2 * sizeof(ulong)));
            u0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 3 * sizeof(ulong)));
        }
        return new UInt256(u0, u1, u2, u3);
    }
}
