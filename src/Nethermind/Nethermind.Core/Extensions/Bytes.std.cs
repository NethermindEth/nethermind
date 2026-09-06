// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
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

    /// <summary>Compares the 32 bytes at <paramref name="a"/> with the 32 bytes at <paramref name="b"/>.</summary>
    /// <remarks>Exists as a std/zkevm pair: the guest has no SIMD, where a <see cref="Vector256{T}"/>
    /// comparison expands to a byte-at-a-time element loop. Loads are unaligned, so a caller may pass
    /// any byte offset.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool AreEqual32(ref byte a, ref byte b)
        => Unsafe.ReadUnaligned<Vector256<byte>>(ref a) == Unsafe.ReadUnaligned<Vector256<byte>>(ref b);

    /// <summary>Tests whether all 32 bytes at <paramref name="a"/> are zero.</summary>
    /// <remarks><inheritdoc cref="AreEqual32" path="/remarks"/></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsZero32(ref byte a)
        => Unsafe.ReadUnaligned<Vector256<byte>>(ref a) == default;
}
