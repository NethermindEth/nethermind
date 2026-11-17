// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Zkvm.Abstractions;

public static partial class Accelerators
{
    // Every routine here runs to completion on the guest's only thread and never calls back into managed code,
    // so the GC transition would only add a frame and callee-saved register spills around each call.

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_blake2f(
        uint rounds,
        Span<byte> h,
        ReadOnlySpan<byte> m,
        ReadOnlySpan<byte> t,
        byte f
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_bls12_g1_add(
        ReadOnlySpan<byte> p1,
        ReadOnlySpan<byte> p2,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_bls12_g1_msm(
        ReadOnlySpan<byte> pairs,
        nuint num_pairs,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_bls12_g2_add(
        ReadOnlySpan<byte> p1,
        ReadOnlySpan<byte> p2,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_bls12_g2_msm(
        ReadOnlySpan<byte> pairs,
        nuint num_pairs,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_bls12_map_fp_to_g1(
        ReadOnlySpan<byte> field_element,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_bls12_map_fp2_to_g2(
        ReadOnlySpan<byte> field_element,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_bls12_pairing(
        ReadOnlySpan<byte> pairs,
        nuint num_pairs,
        [MarshalAs(UnmanagedType.U1)] out bool verified
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_bn254_g1_add(
        ReadOnlySpan<byte> p1,
        ReadOnlySpan<byte> p2,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_bn254_g1_mul(
        ReadOnlySpan<byte> point,
        ReadOnlySpan<byte> scalar,
        Span<byte> result
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_bn254_pairing(
        ReadOnlySpan<byte> pairs,
        nuint num_pairs,
        [MarshalAs(UnmanagedType.U1)] out bool verified
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_keccak256(ReadOnlySpan<byte> data, nuint len, Span<byte> output);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_kzg_point_eval(
        ReadOnlySpan<byte> commitment,
        ReadOnlySpan<byte> z,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> proof,
        [MarshalAs(UnmanagedType.U1)] out bool verified
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_modexp(
        ReadOnlySpan<byte> @base,
        nuint base_len,
        ReadOnlySpan<byte> exp,
        nuint exp_len,
        ReadOnlySpan<byte> modulus,
        nuint mod_len,
        Span<byte> output
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_ripemd160(ReadOnlySpan<byte> data, nuint len, Span<byte> output);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_secp256k1_ecrecover(
        ReadOnlySpan<byte> msg,
        ReadOnlySpan<byte> sig,
        byte recid,
        Span<byte> output
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_secp256k1_verify(
        ReadOnlySpan<byte> msg,
        ReadOnlySpan<byte> sig,
        ReadOnlySpan<byte> pubkey,
        [MarshalAs(UnmanagedType.U1)] out bool verified
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_secp256r1_verify(
        ReadOnlySpan<byte> msg,
        ReadOnlySpan<byte> sig,
        ReadOnlySpan<byte> pubkey,
        [MarshalAs(UnmanagedType.U1)] out bool verified
    );

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial Status zkvm_sha256(ReadOnlySpan<byte> data, nuint len, Span<byte> output);
}
