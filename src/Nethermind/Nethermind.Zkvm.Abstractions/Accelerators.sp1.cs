// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Zkvm.Abstractions;

public static partial class Accelerators
{
    /// <summary>
    /// Extends a SHA-256 message schedule: fills words 16 to 63 from the first sixteen.
    /// </summary>
    /// <param name="w">The 64-word schedule.</param>
    /// <remarks>
    /// Calls SP1's <c>sha256_extend</c> precompile. The schedule holds each 32-bit word, as SHA-256 reads it
    /// big-endian, in the low half of a 64-bit slot. Unchecked for hot paths: the caller guarantees 64 slots,
    /// 8-byte aligned.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Sha256Extend(ulong* w) => zkvm_sha256_extend(w);

    /// <summary>
    /// Performs the SHA-256 compression function on a state and one extended message schedule.
    /// </summary>
    /// <param name="w">The 64-word schedule, extended by <see cref="Sha256Extend"/>.</param>
    /// <param name="state">The eight 32-bit state words, updated in place.</param>
    /// <remarks>
    /// Calls SP1's <c>sha256_compress</c> precompile. Both buffers hold one word per 64-bit slot as described on
    /// <see cref="Sha256Extend"/>, 8-byte aligned and not overlapping. Padding the message is up to the caller.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Sp1Sha256Compress(ulong* w, ulong* state) => zkvm_sha256_compress(w, state);

    /// <summary>
    /// Computes <c>x = (x * y) mod m</c> for 256-bit integers in place, with <c>m</c> the 256-bit integer that
    /// follows <paramref name="y"/>.
    /// </summary>
    /// <param name="x">The multiplicand, overwritten with the result.</param>
    /// <param name="y">The multiplier, immediately followed by the modulus. A zero modulus means 2^256.</param>
    /// <remarks>
    /// Calls SP1's <c>uint256_mulmod</c> precompile. Every integer is stored as four 64-bit limbs, least
    /// significant first, 8-byte aligned. Unchecked for hot paths: the caller guarantees 32 bytes at
    /// <paramref name="x"/> and 64 at <paramref name="y"/>, not overlapping.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void UInt256MulMod(ulong* x, ulong* y) => zkvm_uint256_mulmod(x, y);
}
