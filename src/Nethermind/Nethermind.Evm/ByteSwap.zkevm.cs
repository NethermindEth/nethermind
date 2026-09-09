// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm;

internal static partial class ByteSwap
{
    // riscv64 has no rev8 the toolchain will emit, so BinaryPrimitives.ReverseEndianness(ulong)
    // expands to two 32-bit reversals - and ILC rematerialises each of their masks at every inlined
    // use, which its preinitialiser also does to `static readonly` scalars. An array element is
    // opaque to the preinitialiser, so these load once and stay in registers across the four limbs
    // of a 256-bit word. Doing the swap 64 bits wide at the same time halves the shift-and-mask work.
    private static readonly ulong[] Masks = [0x00FF00FF00FF00FFUL, 0x0000FFFF0000FFFFUL];

    /// <inheritdoc cref="ByteSwap.Reverse(ulong)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Reverse(ulong value)
    {
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(Masks);
        ulong byteMask = masks;
        ulong shortMask = Unsafe.Add(ref masks, 1);

        value = ((value & byteMask) << 8) | ((value >> 8) & byteMask);
        value = ((value & shortMask) << 16) | ((value >> 16) & shortMask);
        return (value << 32) | (value >> 32);
    }
}
