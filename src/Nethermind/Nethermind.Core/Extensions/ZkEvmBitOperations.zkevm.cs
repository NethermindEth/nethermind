// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Extensions;

/// <summary>Byte-swapping primitives for the RISC-V guest.</summary>
public static partial class ZkEvmBitOperations
{
    private static readonly ulong[] SwapMasks = [0x00FF00FF00FF00FFUL, 0x0000FFFF0000FFFFUL];

    /// <summary>Whether the guest's zkVM reverses a word's bytes in one instruction (Zbb's <c>rev8</c>).</summary>
    /// <remarks>
    /// False here, for the zkVMs that decode rv64im only. A guest whose zkVM has Zbb substitutes true in its
    /// substitutions.xml, and ILC folds every branch on it away, so each guest compiles only its own form:
    /// <see cref="BinaryPrimitives.ReverseEndianness(ulong)"/> is one <c>rev8</c> with Zbb and a slower
    /// software sequence than <see cref="Swap"/> without it.
    /// </remarks>
    public static bool HasByteReverse => false;

    // Without Zbb, RISC-V has no byte-swap instruction and this all-64-bit form beats the BCL's ReverseEndianness.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bswap64(ulong x)
    {
        if (HasByteReverse) return BinaryPrimitives.ReverseEndianness(x);
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(SwapMasks);
        return Swap(x, masks, Unsafe.Add(ref masks, 1));
    }

    /// <summary>Loads the swap masks into locals, so a run of <see cref="Swap"/> calls shares them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void LoadSwapMasks(out ulong m8, out ulong m16)
    {
        if (HasByteReverse)
        {
            m8 = m16 = 0;
            return;
        }

        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(SwapMasks);
        m8 = masks;
        m16 = Unsafe.Add(ref masks, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Swap(ulong x, ulong m8, ulong m16)
    {
        if (HasByteReverse) return BinaryPrimitives.ReverseEndianness(x);

        // Addition rather than disjunction: the prover charges `or` 60 units against `add`'s 15.5, and
        // each pair below is disjoint by construction - the masked halves occupy alternating byte, then
        // halfword, then word lanes - so the operators are equivalent here at a quarter of the price.
        // Do not carry this over to a pair that can overlap; there the addition would carry.
        x = ((x & m8) << 8) + ((x >> 8) & m8);
        x = ((x & m16) << 16) + ((x >> 16) & m16);
        return (x << 32) + (x >> 32);
    }

}
