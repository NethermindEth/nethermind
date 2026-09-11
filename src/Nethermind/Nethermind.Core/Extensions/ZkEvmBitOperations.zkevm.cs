// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Extensions;

/// <summary>Byte-swapping primitives for the RISC-V guest.</summary>
public static partial class ZkEvmBitOperations
{
    private static readonly ulong[] SwapMasks = [0x00FF00FF00FF00FFUL, 0x0000FFFF0000FFFFUL];

    // RISC-V has no byte-swap instruction; this all-64-bit form beats the BCL's ReverseEndianness.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bswap64(ulong x)
    {
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(SwapMasks);
        return Swap(x, masks, Unsafe.Add(ref masks, 1));
    }

    /// <summary>Loads the swap masks into locals, so a run of <see cref="Swap"/> calls shares them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void LoadSwapMasks(out ulong m8, out ulong m16)
    {
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(SwapMasks);
        m8 = masks;
        m16 = Unsafe.Add(ref masks, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Swap(ulong x, ulong m8, ulong m16)
    {
        // Addition rather than disjunction: the prover charges `or` 60 units against `add`'s 15.5, and
        // each pair below is disjoint by construction - the masked halves occupy alternating byte, then
        // halfword, then word lanes - so the operators are equivalent here at a quarter of the price.
        // Do not carry this over to a pair that can overlap; there the addition would carry.
        x = ((x & m8) << 8) + ((x >> 8) & m8);
        x = ((x & m16) << 16) + ((x >> 16) & m16);
        return (x << 32) + (x >> 32);
    }

}
