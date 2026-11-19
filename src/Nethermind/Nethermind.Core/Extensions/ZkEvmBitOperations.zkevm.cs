// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Extensions;

/// <summary>Byte-swapping primitives for the RISC-V guest.</summary>
public static partial class ZkEvmBitOperations
{
    private static readonly ulong[] SwapMasks = [0x00FF00FF00FF00FFUL, 0x0000FFFF0000FFFFUL];

    /// <summary>Whether the guest's zkVM reverses a word's bytes in one instruction (Zbb's <c>rev8</c>).</summary>
    /// <remarks>
    /// Of the guests' zkVMs only ZisK has Zbb; the others decode rv64im only. ILC folds every branch on
    /// <see cref="ZiskFlag"/> away, so each guest compiles only its own form:
    /// <see cref="BinaryPrimitives.ReverseEndianness(ulong)"/> is one <c>rev8</c> with Zbb and a slower
    /// software sequence than <see cref="Swap"/> without it.
    /// </remarks>
    internal static bool HasByteReverse => ZiskFlag.IsActive;

    /// <summary>Whether the guest's zkVM reverses the bytes of a 32-bit value in one instruction, and nothing more of
    /// Zbb: Airbender, whose machines decode <c>rev8</c> alone. Its loads still have to be aligned.</summary>
    internal static bool HasRev8 => AirbenderFlag.IsActive;

    /// <summary>Each 32-bit half of <paramref name="x"/> reversed and the halves exchanged, as two <c>rev8</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Rev8Halves(ulong x) =>
        ((ulong)BinaryPrimitives.ReverseEndianness((uint)x) << 32) | BinaryPrimitives.ReverseEndianness((uint)(x >> 32));

    /// <summary><see cref="ReadUInt64BigEndian"/> by aligned word loads: the two or three words that hold the eight
    /// bytes, shifted into place and reversed.</summary>
    /// <remarks>Reads no word that holds none of the bytes.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ulong ReadUInt64BigEndianByWords(ref byte source)
    {
        nuint address = (nuint)Unsafe.AsPointer(ref source);
        uint* words = (uint*)(address & ~(nuint)3);
        int shift = (int)(address & 3) << 3;
        uint first = words[0];
        uint second = words[1];
        if (shift != 0)
        {
            uint third = words[2];
            first = (first >> shift) | (second << (32 - shift));
            second = (second >> shift) | (third << (32 - shift));
        }

        return ((ulong)BinaryPrimitives.ReverseEndianness(first) << 32) | BinaryPrimitives.ReverseEndianness(second);
    }

    // Without Zbb, RISC-V has no byte-swap instruction and this all-64-bit form beats the BCL's ReverseEndianness.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bswap64(ulong x)
    {
        if (HasByteReverse) return BinaryPrimitives.ReverseEndianness(x);
        if (HasRev8) return Rev8Halves(x);
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(SwapMasks);
        return Swap(x, masks, Unsafe.Add(ref masks, 1));
    }

    /// <summary>Reads the 8 bytes at <paramref name="source"/>, which need not be aligned, as a big-endian value.</summary>
    /// <remarks>
    /// Without Zbb the bytes are gathered most significant first. The zkVMs without Zbb other than OpenVM also reject
    /// misaligned accesses, so an unaligned load would be gathered byte by byte anyway; gathering in big-endian order
    /// drops the reversal on top of it. OpenVM takes a misaligned load at the cost of an aligned one, so there one
    /// load and <see cref="Bswap64"/> cost less than the gather.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ReadUInt64BigEndian(ref byte source)
    {
        if (HasRev8) return ReadUInt64BigEndianByWords(ref source);
        return ReadUInt64BigEndianGathered(ref source);
    }

    /// <summary><see cref="ReadUInt64BigEndian"/> without the word loads of <see cref="HasRev8"/>: for sources of no
    /// known alignment in code that has to stay small, such as the PUSH handlers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ReadUInt64BigEndianGathered(ref byte source)
    {
        if (HasByteReverse) return BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref source));
        if (OpenVmFlag.IsActive) return Bswap64(Unsafe.ReadUnaligned<ulong>(ref source));
        if (IntPtr.Size == sizeof(uint))
        {
            // On a 32-bit guest each half is a register: gathered as two 32-bit words, the joining shift is free.
            uint high = ((uint)source << 24) | ((uint)Unsafe.Add(ref source, 1) << 16) |
                ((uint)Unsafe.Add(ref source, 2) << 8) | Unsafe.Add(ref source, 3);
            uint low = ((uint)Unsafe.Add(ref source, 4) << 24) | ((uint)Unsafe.Add(ref source, 5) << 16) |
                ((uint)Unsafe.Add(ref source, 6) << 8) | Unsafe.Add(ref source, 7);
            return ((ulong)high << 32) | low;
        }

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
        if (HasRev8)
        {
            unsafe
            {
                if (((nuint)Unsafe.AsPointer(ref destination) & 3) == 0)
                {
                    Unsafe.As<byte, uint>(ref destination) = BinaryPrimitives.ReverseEndianness((uint)(value >> 32));
                    Unsafe.As<byte, uint>(ref Unsafe.Add(ref destination, 4)) = BinaryPrimitives.ReverseEndianness((uint)value);
                    return;
                }
            }
        }

        if (IntPtr.Size == sizeof(uint))
        {
            uint high = (uint)(value >> 32);
            uint low = (uint)value;
            destination = (byte)(high >> 24);
            Unsafe.Add(ref destination, 1) = (byte)(high >> 16);
            Unsafe.Add(ref destination, 2) = (byte)(high >> 8);
            Unsafe.Add(ref destination, 3) = (byte)high;
            Unsafe.Add(ref destination, 4) = (byte)(low >> 24);
            Unsafe.Add(ref destination, 5) = (byte)(low >> 16);
            Unsafe.Add(ref destination, 6) = (byte)(low >> 8);
            Unsafe.Add(ref destination, 7) = (byte)low;
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

    /// <summary>Whether the <paramref name="length"/> bytes at <paramref name="bytes"/>, 4 or 20 of them, are all 0xff.</summary>
    /// <remarks>
    /// The bytes are ANDed one at a time, for the zkVMs that reject misaligned loads. The ANDs are written out, as the
    /// JIT keeps a loop over the bytes, which costs five instructions a byte.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool AreAllOnes(ref byte bytes, int length)
    {
        Debug.Assert(length is 4 or 20);
        uint bits = AndOfFourBytes(ref bytes, 0);
        if (length == 20)
        {
            bits &= AndOfFourBytes(ref bytes, 4) & AndOfFourBytes(ref bytes, 8) &
                AndOfFourBytes(ref bytes, 12) & AndOfFourBytes(ref bytes, 16);
        }

        return bits == byte.MaxValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AndOfFourBytes(ref byte bytes, nint offset) =>
        (uint)(Unsafe.Add(ref bytes, offset) & Unsafe.Add(ref bytes, offset + 1) &
            Unsafe.Add(ref bytes, offset + 2) & Unsafe.Add(ref bytes, offset + 3));

    /// <summary>Loads the swap masks into locals, so a run of <see cref="Swap"/> calls shares them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void LoadSwapMasks(out ulong m8, out ulong m16)
    {
        if (HasByteReverse || HasRev8)
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
        if (HasRev8) return Rev8Halves(x);

        // A 32-bit guest swaps each half, which then trade places for free, with `or`: a 64-bit addition there is
        // a carry chain of four instructions.
        if (AirbenderFlag.IsActive)
            return ((ulong)Swap32((uint)x, (uint)m8) << 32) | Swap32((uint)(x >> 32), (uint)m8);

        // Addition rather than disjunction: the prover charges `or` 60 units against `add`'s 15.5, and
        // each pair below is disjoint by construction - the masked halves occupy alternating byte, then
        // halfword, then word lanes - so the operators are equivalent here at a quarter of the price.
        // Do not carry this over to a pair that can overlap; there the addition would carry.
        x = ((x & m8) << 8) + ((x >> 8) & m8);
        x = ((x & m16) << 16) + ((x >> 16) & m16);
        return (x << 32) + (x >> 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Swap32(uint x, uint m8)
    {
        x = ((x & m8) << 8) | ((x >> 8) & m8);
        return (x << 16) | (x >> 16);
    }

}
