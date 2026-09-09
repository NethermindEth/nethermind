// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm;

internal static partial class ByteSwap
{
    // riscv64 has no rev8 the toolchain will emit, so BinaryPrimitives.ReverseEndianness(ulong)
    // expands to two 32-bit reversals, and ILC rematerialises each of their masks at every inlined
    // use - as its preinitialiser also does to `static readonly` scalars. An array element is opaque
    // to the preinitialiser. Doing the swap 64 bits wide at the same time halves the shift-and-mask
    // work against two 32-bit halves.
    private static readonly ulong[] MaskValues = [0x00FF00FF00FF00FFUL, 0x0000FFFF0000FFFFUL];

    /// <inheritdoc cref="ByteSwap.Reverse(ulong)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Reverse(ulong value) => Hoist().Reverse(value);

    /// <inheritdoc cref="ByteSwap.Hoist"/>
    /// <remarks>ILC re-forms the array's address for roughly every inlined use rather than once per
    /// method, so a run of swaps - the four limbs of a 256-bit word, times operands and result - pays
    /// for it repeatedly. Holding the masks in a local across the run costs one load each instead.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Swapper Hoist()
    {
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(MaskValues);
        return new Swapper(masks, Unsafe.Add(ref masks, 1));
    }

    /// <inheritdoc cref="ByteSwap.Hoist"/>
    internal readonly struct Swapper(ulong byteMask, ulong shortMask)
    {
        private readonly ulong _byteMask = byteMask;
        private readonly ulong _shortMask = shortMask;

        /// <inheritdoc cref="ByteSwap.Reverse(ulong)"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Reverse(ulong value)
        {
            value = ((value & _byteMask) << 8) | ((value >> 8) & _byteMask);
            value = ((value & _shortMask) << 16) | ((value >> 16) & _shortMask);
            return (value << 32) | (value >> 32);
        }
    }
}
