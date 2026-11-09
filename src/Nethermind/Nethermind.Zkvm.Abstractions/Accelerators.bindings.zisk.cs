// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Zkvm.Abstractions;

public static partial class Accelerators
{
    // TODO: Remove when added to the zkVM standards: https://github.com/eth-act/zkvm-standards/issues/23
    // Two shapes of one import: behind the length check the Span stub costs fewer guest steps, and without it
    // the ref one does.
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial void syscall_keccak_f(Span<ulong> state);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial void syscall_keccak_f(ref ulong state);

    // Since ZisK v1.4.0 the precompile takes the state and the input directly, with no parameter block.
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void syscall_sha256_f(ulong* state, ulong* input);

    // ZisK's 256-bit arithmetic precompiles themselves. Each takes the address of its operand block, which a JIT
    // that lists the import in JitZkCsrPInvokes emits as the CSR write rather than a call.
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void syscall_arith256(Arith256Parameters* parameters);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void syscall_arith256_mod(Arith256ModParameters* parameters);

    // ZisK's EVM JUMPDEST bitmap precompile, a csrs/add pair the transpiler fuses only while they are adjacent.
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void zkvm_jump_dest_bitmap(ulong* bitmap, byte* code, nuint length);

    // ZisK's 256-bit arithmetic on its arith256 precompiles, which the zkVM standards do not cover.
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void add_mod256_c(ulong* a, ulong* b, ulong* modulus, ulong* result);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void div_rem256_c(ulong* a, ulong* b, ulong* quotient, ulong* remainder);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void mul_mod256_c(ulong* a, ulong* b, ulong* modulus, ulong* result);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void reduce_mod256_c(ulong* a, ulong* modulus, ulong* result);

    // ZisK runs the C library's memmove, memset and memcmp as DMA precompiles. Pointers rather than refs:
    // ref arguments make the stub pin them in a stack frame.
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void* memmove(void* dest, void* src, nuint n);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void* memset(void* s, int c, nuint n);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int memcmp(void* s1, void* s2, nuint n);

    // ZisK 1.4's own library routines (definitions/src/zkvmcall.rs), each a call thunk in the runtime that the
    // transpiler turns into a jump to the routine. The zisklib_ ones take the C ABI of the standard zkvm_ imports
    // and are named apart from them, as the ZisK Rust crate still exports those.
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial Status zkvm_u256_le_div(ulong* a, ulong* b, ulong* q);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial Status zisklib_keccak256(byte* data, nuint len, byte* output);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zisklib_secp256k1_ecrecover(
        ReadOnlySpan<byte> msg,
        ReadOnlySpan<byte> sig,
        byte recid,
        Span<byte> output
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zisklib_bn254_g1_add(
        ReadOnlySpan<byte> p1,
        ReadOnlySpan<byte> p2,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zisklib_bn254_g1_mul(
        ReadOnlySpan<byte> point,
        ReadOnlySpan<byte> scalar,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zisklib_bn254_pairing(
        ReadOnlySpan<byte> pairs,
        nuint num_pairs,
        [MarshalAs(UnmanagedType.U1)] out bool verified
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zisklib_kzg_point_eval(
        ReadOnlySpan<byte> commitment,
        ReadOnlySpan<byte> z,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> proof,
        [MarshalAs(UnmanagedType.U1)] out bool verified
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zisklib_bls12_g1_add(
        ReadOnlySpan<byte> p1,
        ReadOnlySpan<byte> p2,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zisklib_bls12_g1_msm(
        ReadOnlySpan<byte> pairs,
        nuint num_pairs,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zisklib_bls12_g2_add(
        ReadOnlySpan<byte> p1,
        ReadOnlySpan<byte> p2,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zisklib_bls12_g2_msm(
        ReadOnlySpan<byte> pairs,
        nuint num_pairs,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zisklib_bls12_pairing(
        ReadOnlySpan<byte> pairs,
        nuint num_pairs,
        [MarshalAs(UnmanagedType.U1)] out bool verified
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zisklib_bls12_map_fp_to_g1(
        ReadOnlySpan<byte> field_element,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zisklib_bls12_map_fp2_to_g2(
        ReadOnlySpan<byte> field_element,
        Span<byte> result
    );
}
