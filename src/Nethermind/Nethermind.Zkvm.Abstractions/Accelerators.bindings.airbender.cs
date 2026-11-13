// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Zkvm.Abstractions;

public static partial class Accelerators
{
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial uint airbender_u256_op(void* a, void* b, uint op);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void airbender_keccak256(byte* data, nuint len, byte* @out);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void airbender_keccak256_laid_out(byte* data, nuint len, byte* @out);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void airbender_keccak_f1600_in_place(ulong* state);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void airbender_secp256k1_ecrecover_batch(
        byte* msgs, byte* sigs, byte* recids, byte* outputs, byte* ok, uint count);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int airbender_secp256k1_verify_batch(
        byte* msgs, byte* sigs, byte* recids, byte* keys, byte* rys, byte* weights, uint count);
}
