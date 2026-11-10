// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Zkvm.Abstractions;

public static partial class Accelerators
{
    /// <summary>
    /// Computes <c>(a + b) mod modulus</c> for 256-bit integers, with the sum taken over 257 bits.
    /// </summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <param name="modulus">The modulus. A zero modulus gives zero.</param>
    /// <param name="result">The buffer to receive the result.</param>
    /// <remarks><inheritdoc cref="MulMod256" path="/remarks"/></remarks>
    public static unsafe void AddMod256(ulong* a, ulong* b, ulong* modulus, ulong* result) =>
        add_mod256_c(a, b, modulus, result);

    /// <summary>
    /// Computes <c>a * b + c</c> for 256-bit integers, with the 512-bit result split into its low and high halves.
    /// </summary>
    /// <param name="a">The multiplicand.</param>
    /// <param name="b">The multiplier.</param>
    /// <param name="c">The addend.</param>
    /// <param name="low">The buffer to receive the low 256 bits.</param>
    /// <param name="high">The buffer to receive the high 256 bits.</param>
    /// <remarks>
    /// Runs ZisK's <c>arith256</c> precompile directly, with no library routine around it; a JIT that lists
    /// <c>syscall_arith256</c> in <c>JitZkCsrPInvokes</c> emits it as the single CSR write. The operand block is built
    /// on the caller's stack and not cleared first, as every field is written.
    /// <inheritdoc cref="MulMod256" path="/remarks"/>
    /// </remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Arith256(ulong* a, ulong* b, ulong* c, ulong* low, ulong* high)
    {
        Unsafe.SkipInit(out Arith256Parameters parameters);
        parameters.A = a;
        parameters.B = b;
        parameters.C = c;
        parameters.Low = low;
        parameters.High = high;
        syscall_arith256(&parameters);
    }

    /// <summary>
    /// Runs ZisK's <c>arith256</c> precompile on the operand block <paramref name="parameters"/> points to.
    /// </summary>
    /// <param name="parameters">The operand block. It is only read, so a caller may reuse it across calls.</param>
    /// <remarks>The contract of <see cref="Arith256(ulong*, ulong*, ulong*, ulong*, ulong*)"/> applies to its pointers.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Arith256(Arith256Parameters* parameters) => syscall_arith256(parameters);

    /// <summary>
    /// Computes <c>(a * b + c) mod modulus</c> for 256-bit integers, with the product and sum taken over 512 bits.
    /// </summary>
    /// <param name="a">The multiplicand.</param>
    /// <param name="b">The multiplier.</param>
    /// <param name="c">The addend.</param>
    /// <param name="modulus">The modulus. It must not be zero.</param>
    /// <param name="result">The buffer to receive the result.</param>
    /// <remarks>
    /// Runs ZisK's <c>arith256_mod</c> precompile directly, where <see cref="MulMod256"/>, <see cref="AddMod256"/> and
    /// <see cref="ReduceMod256"/> call a library routine around it that also handles a zero modulus: <c>(a * b) mod m</c>
    /// is <c>c = 0</c>, <c>(a + c) mod m</c> is <c>b = 1</c>, and <c>a mod m</c> is both. A JIT that lists
    /// <c>syscall_arith256_mod</c> in <c>JitZkCsrPInvokes</c> emits it as the single CSR write. The operand block is
    /// built on the caller's stack and not cleared first, as every field is written.
    /// <inheritdoc cref="MulMod256" path="/remarks"/>
    /// </remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Arith256Mod(ulong* a, ulong* b, ulong* c, ulong* modulus, ulong* result)
    {
        Unsafe.SkipInit(out Arith256ModParameters parameters);
        parameters.A = a;
        parameters.B = b;
        parameters.C = c;
        parameters.Modulus = modulus;
        parameters.Result = result;
        syscall_arith256_mod(&parameters);
    }

    /// <summary>
    /// Runs ZisK's <c>arith256_mod</c> precompile on the operand block <paramref name="parameters"/> points to.
    /// </summary>
    /// <param name="parameters">The operand block. It is only read, so a caller may reuse it across calls.</param>
    /// <remarks>The contract of <see cref="Arith256Mod(ulong*, ulong*, ulong*, ulong*, ulong*)"/> applies to its pointers.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Arith256Mod(Arith256ModParameters* parameters) => syscall_arith256_mod(parameters);

    /// <summary>
    /// Performs the Keccak-f[1600] permutation on the given state.
    /// </summary>
    /// <param name="state">The state buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException">The <c>state</c> must be 25 elements long.</exception>
    public static void KeccakF(Span<ulong> state)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(state.Length, 25, nameof(state));

        syscall_keccak_f(state);
    }

    /// <summary>
    /// Performs the Keccak-f[1600] permutation in place on the 25 lanes starting at <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The first of 25 contiguous state lanes.</param>
    /// <remarks>
    /// Unchecked counterpart of <see cref="KeccakF(Span{ulong})"/> for absorb loops that already hold the state:
    /// the caller guarantees 25 lanes are addressable from <paramref name="state"/>.
    /// </remarks>
    public static void KeccakF(ref ulong state) => syscall_keccak_f(ref state);

    /// <summary>
    /// Marks in <paramref name="bitmap"/> every JUMPDEST of the EVM code at <paramref name="code"/>.
    /// </summary>
    /// <param name="bitmap">The bitmap, one bit per code byte, least significant bit first; a set bit is a JUMPDEST.</param>
    /// <param name="code">The code.</param>
    /// <param name="length">The code's length in bytes. Zero leaves the bitmap untouched without accessing either pointer.</param>
    /// <remarks>
    /// Runs ZisK's JUMPDEST bitmap precompile. Both pointers must be 8-byte aligned; the precompile reads and
    /// writes whole words, so it may read up to seven bytes past the code. A JIT that lists
    /// <c>zkvm_jump_dest_bitmap</c> in <c>JitZkCsrPInvokes</c> emits the precompile's CSR write and the <c>add</c>
    /// after it in place of the call.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void JumpDestBitmap(ulong* bitmap, byte* code, nuint length)
    {
        // ZisK cannot prove a zero-length bitmap operation.
        if (length == 0)
            return;

        zkvm_jump_dest_bitmap(bitmap, code, length);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to the start of <paramref name="destination"/>; the two may overlap.
    /// </summary>
    /// <param name="source">The bytes to copy.</param>
    /// <param name="destination">The buffer to receive them.</param>
    /// <remarks>
    /// Calls ZisK's <c>memmove</c> precompile directly. Corelib's span copy reaches the same routine for longer runs,
    /// but through a wrapper that spills every callee-saved register. The spans are not pinned: a guest is
    /// single-threaded and collects only when it allocates, so no GC can run between taking their addresses and the
    /// call returning. That does not hold on a multithreaded host.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The <c>destination</c> must be at least as long as <c>source</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Memmove(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(source.Length, destination.Length, nameof(source));

        unsafe
        {
            memmove(
                Unsafe.AsPointer(ref MemoryMarshal.GetReference(destination)),
                Unsafe.AsPointer(ref MemoryMarshal.GetReference(source)),
                (nuint)source.Length);
        }
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes from <paramref name="source"/> to <paramref name="destination"/>;
    /// the two may overlap.
    /// </summary>
    /// <param name="destination">The buffer to receive the bytes.</param>
    /// <param name="source">The bytes to copy.</param>
    /// <param name="length">The number of bytes to copy.</param>
    /// <remarks>
    /// Unchecked counterpart of <see cref="Memmove(ReadOnlySpan{byte}, Span{byte})"/> for hot paths: the caller
    /// guarantees both buffers hold <paramref name="length"/> bytes, and that pointers into managed memory stay
    /// valid until the call returns. Raw pointers skip the stub that pins <c>ref</c> arguments in a stack frame.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Memmove(void* destination, void* source, nuint length) =>
        memmove(destination, source, length);

    /// <summary>
    /// Sets <paramref name="length"/> bytes at <paramref name="destination"/> to <paramref name="value"/>.
    /// </summary>
    /// <param name="destination">The buffer to fill.</param>
    /// <param name="value">The byte to fill it with.</param>
    /// <param name="length">The number of bytes to fill.</param>
    /// <remarks>
    /// Calls ZisK's <c>memset</c> precompile directly. Unchecked, with the caller's contract of
    /// <see cref="Memmove(void*, void*, nuint)"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Memset(void* destination, byte value, nuint length) =>
        memset(destination, value, length);

    /// <summary>
    /// Compares <paramref name="length"/> bytes at <paramref name="left"/> with those at <paramref name="right"/>.
    /// </summary>
    /// <param name="left">The first buffer.</param>
    /// <param name="right">The second buffer.</param>
    /// <param name="length">The number of bytes to compare.</param>
    /// <returns>
    /// Zero if the bytes are equal; otherwise negative or positive as the first differing byte, taken as unsigned,
    /// is lower or higher in <paramref name="left"/>.
    /// </returns>
    /// <remarks>
    /// Calls ZisK's <c>memcmp</c> precompile directly. Unchecked, with the caller's contract of
    /// <see cref="Memmove(void*, void*, nuint)"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int Memcmp(void* left, void* right, nuint length) =>
        memcmp(left, right, length);

    /// <summary>
    /// Computes <c>(a * b) mod modulus</c> for 256-bit integers, with the product taken over 512 bits.
    /// </summary>
    /// <param name="a">The multiplicand.</param>
    /// <param name="b">The multiplier.</param>
    /// <param name="modulus">The modulus. A zero modulus gives zero.</param>
    /// <param name="result">The buffer to receive the result.</param>
    /// <remarks>
    /// Every argument points to a 256-bit integer stored as four 64-bit limbs, least significant first. An
    /// output must not alias an input: ZisK takes them as Rust references, which it may assume never
    /// overlap. These take pointers rather than spans so that hot callers skip the length checks and pinning,
    /// and so that a caller shared with other zkVMs can hold them in <c>delegate*&lt;ulong*, ...&gt;</c> slots
    /// and use them only where ZisK installs them.
    /// </remarks>
    public static unsafe void MulMod256(ulong* a, ulong* b, ulong* modulus, ulong* result) =>
        mul_mod256_c(a, b, modulus, result);

    /// <summary>
    /// Computes <c>a mod modulus</c> for 256-bit integers.
    /// </summary>
    /// <param name="a">The value to reduce.</param>
    /// <param name="modulus">The modulus. A zero modulus gives zero.</param>
    /// <param name="result">The buffer to receive the result.</param>
    /// <remarks><inheritdoc cref="MulMod256" path="/remarks"/></remarks>
    public static unsafe void ReduceMod256(ulong* a, ulong* modulus, ulong* result) =>
        reduce_mod256_c(a, modulus, result);

    /// <summary>
    /// Performs the SHA-256 compression function on a state and one 64-byte block.
    /// </summary>
    /// <param name="state">The eight 32-bit state words, as four 64-bit lanes with the lower-indexed word in the low half.</param>
    /// <param name="block">The message block, as its 64 bytes in order.</param>
    /// <remarks>
    /// Both pointers must be 8-byte aligned, and <paramref name="block"/> may not overlap <paramref name="state"/>.
    /// Padding the message is up to the caller. Unchecked, like <see cref="KeccakF(ref ulong)"/>, as merkleization
    /// runs it twice per node.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Sha256F(ulong* state, ulong* block) => syscall_sha256_f(state, block);

    /// <summary>
    /// Computes <c>a / b</c> for 256-bit integers.
    /// </summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor. A zero divisor gives zero.</param>
    /// <param name="quotient">The buffer to receive the quotient. It may alias an operand.</param>
    /// <remarks>
    /// Calls ZisK's little-endian U256 library routine (<c>ziskasm/lang/c/include/zkvm_u256_le.h</c>), which reads both
    /// operands before it writes the quotient. Every argument points to four 64-bit limbs, least significant first.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Div256(ulong* a, ulong* b, ulong* quotient) => zkvm_u256_le_div(a, b, quotient);

    /// <inheritdoc cref="Sha256"/>
    /// <remarks>Runs ZisK's own SHA-256 library routine, which always succeeds.</remarks>
    public static void ZiskSha256(ReadOnlySpan<byte> data, Span<byte> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, 32, nameof(output));

        zisklib_sha256(data, (nuint)data.Length, output);
    }

    /// <inheritdoc cref="ModExp"/>
    /// <remarks>Runs ZisK's own modular exponentiation library routine.</remarks>
    public static void ZiskModExp(ReadOnlySpan<byte> @base, ReadOnlySpan<byte> exp, ReadOnlySpan<byte> modulus, Span<byte> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, modulus.Length, nameof(output));

        Status status = zisklib_modexp(@base, (nuint)@base.Length, exp, (nuint)exp.Length, modulus, (nuint)modulus.Length, output);

        ThrowIfFailed(status, nameof(zisklib_modexp));
    }

    /// <summary>
    /// Computes the Keccak-256 hash of <paramref name="length"/> bytes at <paramref name="data"/> into the 32 bytes at
    /// <paramref name="output"/>.
    /// </summary>
    /// <param name="data">The data to hash.</param>
    /// <param name="length">The number of bytes to hash.</param>
    /// <param name="output">The buffer to receive the hash value.</param>
    /// <remarks>
    /// Runs ZisK's own sponge library routine, which always succeeds. Unchecked counterpart of
    /// <see cref="Keccak256(ReadOnlySpan{byte}, Span{byte})"/> for hot paths, with the caller's contract of
    /// <see cref="Memmove(void*, void*, nuint)"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void ZiskKeccak256(byte* data, nuint length, byte* output) =>
        zisklib_keccak256(data, length, output);

    /// <inheritdoc cref="SecP256k1Recover"/>
    /// <remarks>Runs ZisK's own recovery library routine.</remarks>
    public static Status ZiskSecP256k1Recover(
        ReadOnlySpan<byte> msg,
        ReadOnlySpan<byte> sig,
        byte recid,
        Span<byte> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(msg.Length, 32, nameof(msg));
        ArgumentOutOfRangeException.ThrowIfNotEqual(sig.Length, 64, nameof(sig));
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, 64, nameof(output));

        return zisklib_secp256k1_ecrecover(msg, sig, recid, output);
    }

    /// <inheritdoc cref="BN254G1Add"/>
    /// <remarks>Runs ZisK's own BN254 library routine.</remarks>
    public static Status ZiskBN254G1Add(ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(p1.Length, 64, nameof(p1));
        ArgumentOutOfRangeException.ThrowIfNotEqual(p2.Length, 64, nameof(p2));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 64, nameof(result));

        return zisklib_bn254_g1_add(p1, p2, result);
    }

    /// <inheritdoc cref="BN254G1Mul"/>
    /// <remarks>Runs ZisK's own BN254 library routine.</remarks>
    public static Status ZiskBN254G1Mul(ReadOnlySpan<byte> point, ReadOnlySpan<byte> scalar, Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(point.Length, 64, nameof(point));
        ArgumentOutOfRangeException.ThrowIfNotEqual(scalar.Length, 32, nameof(scalar));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 64, nameof(result));

        return zisklib_bn254_g1_mul(point, scalar, result);
    }

    /// <inheritdoc cref="BN254Pairing"/>
    /// <remarks>Runs ZisK's own BN254 library routine.</remarks>
    public static Status ZiskBN254Pairing(ReadOnlySpan<byte> pairs, nuint numPairs, out bool verified)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)pairs.Length, 192 * numPairs, nameof(pairs));

        return zisklib_bn254_pairing(pairs, numPairs, out verified);
    }

    /// <inheritdoc cref="KzgPointEval"/>
    /// <remarks>Runs ZisK's own KZG library routine.</remarks>
    public static bool ZiskKzgPointEval(
        ReadOnlySpan<byte> commitment,
        ReadOnlySpan<byte> z,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> proof)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(commitment.Length, 48, nameof(commitment));
        ArgumentOutOfRangeException.ThrowIfNotEqual(z.Length, 32, nameof(z));
        ArgumentOutOfRangeException.ThrowIfNotEqual(y.Length, 32, nameof(y));
        ArgumentOutOfRangeException.ThrowIfNotEqual(proof.Length, 48, nameof(proof));

        Status status = zisklib_kzg_point_eval(commitment, z, y, proof, out bool verified);

        ThrowIfFailed(status, nameof(zisklib_kzg_point_eval));

        return verified;
    }

    /// <inheritdoc cref="Bls12381G1Add"/>
    /// <remarks>Runs ZisK's own BLS12-381 library routine.</remarks>
    public static Status ZiskBls12381G1Add(ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(p1.Length, 96, nameof(p1));
        ArgumentOutOfRangeException.ThrowIfNotEqual(p2.Length, 96, nameof(p2));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 96, nameof(result));

        return zisklib_bls12_g1_add(p1, p2, result);
    }

    /// <inheritdoc cref="Bls12381G1Msm"/>
    /// <remarks>Runs ZisK's own BLS12-381 library routine.</remarks>
    public static Status ZiskBls12381G1Msm(ReadOnlySpan<byte> pairs, nuint numPairs, Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)pairs.Length, 128 * numPairs, nameof(pairs));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 96, nameof(result));

        return zisklib_bls12_g1_msm(pairs, numPairs, result);
    }

    /// <inheritdoc cref="Bls12381G2Add"/>
    /// <remarks>Runs ZisK's own BLS12-381 library routine.</remarks>
    public static Status ZiskBls12381G2Add(ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(p1.Length, 192, nameof(p1));
        ArgumentOutOfRangeException.ThrowIfNotEqual(p2.Length, 192, nameof(p2));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 192, nameof(result));

        return zisklib_bls12_g2_add(p1, p2, result);
    }

    /// <inheritdoc cref="Bls12381G2Msm"/>
    /// <remarks>Runs ZisK's own BLS12-381 library routine.</remarks>
    public static Status ZiskBls12381G2Msm(ReadOnlySpan<byte> pairs, nuint numPairs, Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)pairs.Length, 224 * numPairs, nameof(pairs));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 192, nameof(result));

        return zisklib_bls12_g2_msm(pairs, numPairs, result);
    }

    /// <inheritdoc cref="Bls12381Pairing"/>
    /// <remarks>Runs ZisK's own BLS12-381 library routine.</remarks>
    public static Status ZiskBls12381Pairing(ReadOnlySpan<byte> pairs, nuint numPairs, out bool verified)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)pairs.Length, 288 * numPairs, nameof(pairs));

        return zisklib_bls12_pairing(pairs, numPairs, out verified);
    }

    /// <inheritdoc cref="Bls12381MapFpToG1"/>
    /// <remarks>Runs ZisK's own BLS12-381 library routine.</remarks>
    public static Status ZiskBls12381MapFpToG1(ReadOnlySpan<byte> fieldElement, Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(fieldElement.Length, 48, nameof(fieldElement));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 96, nameof(result));

        return zisklib_bls12_map_fp_to_g1(fieldElement, result);
    }

    /// <inheritdoc cref="Bls12381MapFp2ToG2"/>
    /// <remarks>Runs ZisK's own BLS12-381 library routine.</remarks>
    public static Status ZiskBls12381MapFp2ToG2(ReadOnlySpan<byte> fieldElement, Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(fieldElement.Length, 96, nameof(fieldElement));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 192, nameof(result));

        return zisklib_bls12_map_fp2_to_g2(fieldElement, result);
    }

    /// <summary>
    /// The operand block ZisK's <c>arith256</c> precompile reads through its single pointer argument.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Arith256Parameters
    {
        /// <summary>The multiplicand.</summary>
        public ulong* A;

        /// <summary>The multiplier.</summary>
        public ulong* B;

        /// <summary>The addend.</summary>
        public ulong* C;

        /// <summary>The buffer that receives the low 256 bits of the result.</summary>
        public ulong* Low;

        /// <summary>The buffer that receives the high 256 bits of the result.</summary>
        public ulong* High;
    }

    /// <summary>
    /// The operand block ZisK's <c>arith256_mod</c> precompile reads through its single pointer argument.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Arith256ModParameters
    {
        /// <summary>The multiplicand.</summary>
        public ulong* A;

        /// <summary>The multiplier.</summary>
        public ulong* B;

        /// <summary>The addend.</summary>
        public ulong* C;

        /// <summary>The modulus, which must not be zero.</summary>
        public ulong* Modulus;

        /// <summary>The buffer that receives the result.</summary>
        public ulong* Result;
    }
}
