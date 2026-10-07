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
    internal static bool HasByteReverse => false;

    // Without Zbb, RISC-V has no byte-swap instruction and this all-64-bit form beats the BCL's ReverseEndianness.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bswap64(ulong x)
    {
        if (HasByteReverse) return BinaryPrimitives.ReverseEndianness(x);
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(SwapMasks);
        return Swap(x, masks, Unsafe.Add(ref masks, 1));
    }

    /// <summary>Reads the 8 bytes at <paramref name="source"/>, which need not be aligned, as a big-endian value.</summary>
    /// <remarks>
    /// Without Zbb the bytes are gathered most significant first. The zkVMs without Zbb also reject misaligned
    /// accesses, so an unaligned load would be gathered byte by byte anyway; gathering in big-endian order drops
    /// the reversal on top of it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ReadUInt64BigEndian(ref byte source)
    {
        if (HasByteReverse) return BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref source));
        return ((ulong)source << 56) | ((ulong)Unsafe.Add(ref source, 1) << 48) |
            ((ulong)Unsafe.Add(ref source, 2) << 40) | ((ulong)Unsafe.Add(ref source, 3) << 32) |
            ((ulong)Unsafe.Add(ref source, 4) << 24) | ((ulong)Unsafe.Add(ref source, 5) << 16) |
            ((ulong)Unsafe.Add(ref source, 6) << 8) | Unsafe.Add(ref source, 7);
    }

    /// <summary>Writes <paramref name="value"/> big-endian to the 8 bytes at <paramref name="destination"/>, which need not be aligned.</summary>
    /// <remarks>The scatter counterpart of <see cref="ReadUInt64BigEndian"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteUInt64BigEndian(ref byte destination, ulong value)
    {
        if (HasByteReverse)
        {
            Unsafe.WriteUnaligned(ref destination, BinaryPrimitives.ReverseEndianness(value));
            return;
        }

        destination = (byte)(value >> 56);
        Unsafe.Add(ref destination, 1) = (byte)(value >> 48);
        Unsafe.Add(ref destination, 2) = (byte)(value >> 40);
        Unsafe.Add(ref destination, 3) = (byte)(value >> 32);
        Unsafe.Add(ref destination, 4) = (byte)(value >> 24);
        Unsafe.Add(ref destination, 5) = (byte)(value >> 16);
        Unsafe.Add(ref destination, 6) = (byte)(value >> 8);
        Unsafe.Add(ref destination, 7) = (byte)value;
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
