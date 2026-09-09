// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Int256;

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

    /// <summary>Writes <paramref name="value"/> to <paramref name="destination"/> with all 32 bytes reversed.</summary>
    /// <remarks>Shares the swap masks across the four lanes and stores lanes directly; per-lane
    /// <see cref="Bswap64"/> calls rematerialize the mask constants for every lane, and composing the
    /// result through <see cref="Vector256"/> round-trips it through memory. Lanes are stored as they
    /// are computed, so <paramref name="destination"/> must not overlap <paramref name="value"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bswap256(in UInt256 value, ref Vector256<byte> destination)
    {
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(SwapMasks);
        ulong m8 = masks;
        ulong m16 = Unsafe.Add(ref masks, 1);
        ref ulong d = ref Unsafe.As<Vector256<byte>, ulong>(ref destination);
        d = Swap(value.u3, m8, m16);
        Unsafe.Add(ref d, 1) = Swap(value.u2, m8, m16);
        Unsafe.Add(ref d, 2) = Swap(value.u1, m8, m16);
        Unsafe.Add(ref d, 3) = Swap(value.u0, m8, m16);
    }

    /// <summary>Reads 32 bytes at <paramref name="source"/> reversed into <paramref name="result"/>.</summary>
    /// <remarks><inheritdoc cref="Bswap256(in UInt256, ref Vector256{byte})" path="/remarks"/></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bswap256(ref readonly byte source, out UInt256 result)
    {
        ref ulong masks = ref MemoryMarshal.GetArrayDataReference(SwapMasks);
        ulong m8 = masks;
        ulong m16 = Unsafe.Add(ref masks, 1);
        ref byte s = ref Unsafe.AsRef(in source);
        Unsafe.SkipInit(out result);
        ref ulong r = ref Unsafe.As<UInt256, ulong>(ref result);
        Unsafe.Add(ref r, 3) = Swap(Unsafe.ReadUnaligned<ulong>(ref s), m8, m16);
        Unsafe.Add(ref r, 2) = Swap(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, 8)), m8, m16);
        Unsafe.Add(ref r, 1) = Swap(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, 16)), m8, m16);
        r = Swap(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, 24)), m8, m16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Swap(ulong x, ulong m8, ulong m16)
    {
        x = ((x & m8) << 8) | ((x >> 8) & m8);
        x = ((x & m16) << 16) | ((x >> 16) & m16);
        return (x << 32) | (x >> 32);
    }

}
