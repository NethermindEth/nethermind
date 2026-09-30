// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Nethermind.Core.Extensions;

public static unsafe partial class Bytes
{
    /// <summary>
    /// Reverses the byte order of a 64-bit word.
    /// </summary>
    /// <remarks>
    /// Named for its width rather than after <see cref="BinaryPrimitives.ReverseEndianness(ulong)"/>:
    /// narrower unsigned arguments widen implicitly, so a name shared with the BCL's twelve overloads
    /// would silently swap an 8-byte zero-extension. Exists as a std/zkevm pair because the fastest
    /// form differs per target; use it wherever a hot path swaps whole words so the guest build picks
    /// up its variant.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Bswap64(ulong value) => BinaryPrimitives.ReverseEndianness(value);

    /// <summary>
    /// Hoists whatever a run of <see cref="Bswap64"/> calls needs, so a caller pays for it once.
    /// </summary>
    /// <remarks>
    /// Nothing to hoist on the host, where the swap is one instruction; the empty struct disappears
    /// when inlined. See <c>Bytes.zkevm.cs</c> for the guest form.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Bswap64Hoist HoistBswap64() => default;

    /// <summary>A run of byte swaps sharing whatever <see cref="HoistBswap64"/> loaded.</summary>
    internal readonly struct Bswap64Hoist
    {
        /// <summary>Reverses the byte order of a 64-bit word.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong Bswap64(ulong value) => BinaryPrimitives.ReverseEndianness(value);
    }

    /// <summary>Compares the 32 bytes at <paramref name="a"/> with the 32 bytes at <paramref name="b"/>.</summary>
    /// <remarks>
    /// Loads are unaligned, so a caller may pass any byte offset.
    /// See <c>Bytes.zkevm.cs</c> for the whole-word guest implementation: without SIMD,
    /// byte-vector comparisons can expand into byte-at-a-time loops.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool AreEqual32(ref byte a, ref byte b)
        => Unsafe.ReadUnaligned<Vector256<ulong>>(ref a) == Unsafe.ReadUnaligned<Vector256<ulong>>(ref b);

    /// <summary>Tests whether all 32 bytes at <paramref name="a"/> are zero.</summary>
    /// <remarks><inheritdoc cref="AreEqual32" path="/remarks"/></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsZero32(ref byte a)
        => Unsafe.ReadUnaligned<Vector256<byte>>(ref a) == default;

    /// <summary>Number of leading zero bytes in a 64-bit value; <c>8</c> when it is zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LeadingZeroBytes(ulong value) => BitOperations.LeadingZeroCount(value) >> 3;

    /// <summary>Number of leading zero bits in a 64-bit value; <c>64</c> when it is zero.</summary>
    /// <remarks>Exists as a std/zkevm pair: one instruction here, a software fallback on the guest.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LeadingZeroBits(ulong value) => BitOperations.LeadingZeroCount(value);
}
