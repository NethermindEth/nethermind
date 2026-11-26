// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Zkvm.Abstractions;

public static partial class Accelerators
{
    // OpenVM's bigint, Keccak and SHA-2 extensions, which the zkVM standards do not cover.
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void zkvm_u256_mul(ulong* result, ulong* a, ulong* b);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void zkvm_keccak_xorin(ulong* buffer, byte* input, nuint len);

    // Same symbol as SP1's, with OpenVM's arguments.
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void zkvm_sha256_compress(ulong* state, ulong* input, ulong* output);

    // bflat-openvm's division hint and the RV64IM routines that check it.
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void zkvm_u256_mul_wide(ulong* x, ulong* y, ulong* product);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void zkvm_u512_divrem_hint(ulong* numerator, ulong* divisor, ulong* quotient_remainder);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int zkvm_u512_is_divrem(ulong* numerator, ulong* divisor, ulong* quotient, ulong* remainder);
}
