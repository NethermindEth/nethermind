// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Zkvm.Abstractions;

public static partial class Accelerators
{
    /// <summary>
    /// Computes <c>(a * b) mod 2^256</c>.
    /// </summary>
    /// <param name="result">The buffer to receive the result.</param>
    /// <param name="a">The multiplicand.</param>
    /// <param name="b">The multiplier.</param>
    /// <remarks>
    /// One instruction of OpenVM's bigint extension. Every argument points to a 256-bit integer stored as four
    /// 64-bit limbs, least significant first. Unchecked for hot paths: the caller guarantees 32 bytes at each
    /// pointer, 8-byte aligned. Both operands are read before the result is written, so
    /// <paramref name="result"/> may alias either.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void UInt256Mul(ulong* result, ulong* a, ulong* b) => zkvm_u256_mul(result, a, b);

    /// <summary>
    /// XORs <paramref name="len"/> bytes of <paramref name="input"/> into the Keccak-f[1600] state <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">The 25-lane state.</param>
    /// <param name="input">The bytes to absorb.</param>
    /// <param name="len">The number of bytes, at most the 136-byte rate: a longer run executes but fails to prove.</param>
    /// <remarks>
    /// One instruction of OpenVM's Keccak extension, the absorb step of a sponge without the permutation.
    /// Unchecked for hot paths: the caller guarantees both pointers are 8-byte aligned and hold
    /// <paramref name="len"/> bytes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void KeccakXorin(ulong* buffer, byte* input, nuint len) => zkvm_keccak_xorin(buffer, input, len);

    /// <summary>
    /// Performs the SHA-256 compression function on a state and one 64-byte block.
    /// </summary>
    /// <param name="state">The eight 32-bit state words, as four 64-bit lanes with the lower-indexed word in the low half.</param>
    /// <param name="input">The message block, as its 64 bytes in order.</param>
    /// <param name="output">The buffer to receive the new state, in the layout of <paramref name="state"/>; it may alias <paramref name="state"/>.</param>
    /// <remarks>
    /// One instruction of OpenVM's SHA-2 extension. Every pointer must be 8-byte aligned. Padding the message is
    /// up to the caller.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void OpenVMSha256Compress(ulong* state, ulong* input, ulong* output) => zkvm_sha256_compress(state, input, output);

    /// <summary>
    /// Computes the full 512-bit product <c>x * y</c>.
    /// </summary>
    /// <param name="x">The multiplicand.</param>
    /// <param name="y">The multiplier.</param>
    /// <param name="product">The buffer to receive the product, as eight 64-bit limbs.</param>
    /// <remarks>
    /// A plain RV64IM routine on <c>MULHU</c>, which managed code cannot reach on this target. Operands are four
    /// 64-bit limbs, least significant first. Unchecked for hot paths: the caller guarantees 32 bytes at
    /// <paramref name="x"/> and <paramref name="y"/> and 64 at <paramref name="product"/>, 8-byte aligned. Both
    /// operands are read before the product is written, so <paramref name="product"/> may alias either.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void UInt256MulWide(ulong* x, ulong* y, ulong* product) => zkvm_u256_mul_wide(x, y, product);

    /// <summary>
    /// Asks the runner for the quotient and remainder of <paramref name="numerator"/> divided by <paramref name="divisor"/>.
    /// </summary>
    /// <param name="numerator">The 512-bit dividend, as eight 64-bit limbs.</param>
    /// <param name="divisor">The 256-bit divisor, as four 64-bit limbs.</param>
    /// <param name="quotientRemainder">The buffer to receive the quotient, truncated to 256 bits, then the remainder.</param>
    /// <remarks>
    /// A phantom instruction that only the bflat-openvm runner registers; any other runner traps on it. The result
    /// is untrusted: accept it only once <see cref="UInt512IsDivRem"/> returns <see langword="true"/>. Limbs are
    /// least significant first. Unchecked for hot paths: the caller guarantees the buffer sizes, 8-byte aligned.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void UInt512DivRemHint(ulong* numerator, ulong* divisor, ulong* quotientRemainder) =>
        zkvm_u512_divrem_hint(numerator, divisor, quotientRemainder);

    /// <summary>
    /// Checks that <paramref name="quotient"/> and <paramref name="remainder"/> are those of
    /// <paramref name="numerator"/> divided by <paramref name="divisor"/>.
    /// </summary>
    /// <param name="numerator">The 512-bit dividend, as eight 64-bit limbs.</param>
    /// <param name="divisor">The 256-bit divisor, as four 64-bit limbs.</param>
    /// <param name="quotient">The claimed quotient, as four 64-bit limbs.</param>
    /// <param name="remainder">The claimed remainder, as four 64-bit limbs.</param>
    /// <returns>
    /// <see langword="true"/> if <c>quotient * divisor + remainder</c> equals <paramref name="numerator"/> over all
    /// 512 bits and <c>remainder &lt; divisor</c>, which pins both values.
    /// </returns>
    /// <remarks>
    /// A plain RV64IM routine, proven like any other guest code. A true quotient of 2^256 or more never passes, so the
    /// caller falls back to software when one is possible. Limbs are least significant first. Unchecked for hot
    /// paths: the caller guarantees the buffer sizes, 8-byte aligned.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe bool UInt512IsDivRem(ulong* numerator, ulong* divisor, ulong* quotient, ulong* remainder) =>
        zkvm_u512_is_divrem(numerator, divisor, quotient, remainder) != 0;
}
