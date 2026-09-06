// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core.Extensions;

public static unsafe partial class Bytes
{
    /// <summary>
    /// Reverses the byte order of a 64-bit word.
    /// </summary>
    /// <remarks>
    /// RISC-V has no byte-swap instruction, so the BCL's <c>ReverseEndianness</c> expands to a
    /// byte-at-a-time shuffle; <see cref="ZkEvmBitOperations.Bswap64"/> does it with three masked
    /// shift/or pairs on whole words. See <c>Bytes.std.cs</c> for the host form.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Bswap64(ulong value) => ZkEvmBitOperations.Bswap64(value);

    /// <summary>Compares the 32 bytes at <paramref name="a"/> with the 32 bytes at <paramref name="b"/>.</summary>
    /// <remarks>
    /// Four whole-word comparisons: the guest has no SIMD, and ILC expands a
    /// <see cref="System.Runtime.Intrinsics.Vector256{T}"/> comparison to a byte-at-a-time element loop
    /// over every lane. Loads are unaligned, so a caller may pass any byte offset.
    /// See <c>Bytes.std.cs</c> for the host form.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool AreEqual32(ref byte a, ref byte b)
        => Unsafe.ReadUnaligned<ulong>(ref a) == Unsafe.ReadUnaligned<ulong>(ref b)
            && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 8)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8))
            && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 16)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 16))
            && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 24)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 24));

    /// <summary>Tests whether all 32 bytes at <paramref name="a"/> are zero.</summary>
    /// <remarks><inheritdoc cref="AreEqual32" path="/remarks"/></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsZero32(ref byte a)
        => (Unsafe.ReadUnaligned<ulong>(ref a)
            | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 8))
            | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 16))
            | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 24))) == 0;
}
