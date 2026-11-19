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

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int airbender_prehashed_code(byte* data, uint index, byte* @out);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void airbender_division_hints(byte** start, uint* count);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int airbender_mpt_attach(byte* first, byte* last, uint count);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int airbender_mpt_has_root(byte* root);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void* airbender_mpt_open(byte* root);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void airbender_mpt_clear(void* trie);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int airbender_mpt_root(void* trie, byte* @out);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int airbender_mpt_get_account(void* trie, byte* address, byte** value, uint* length);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int airbender_mpt_set_account(void* trie, byte* address, byte* value, uint length);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int airbender_mpt_get_slot(void* trie, uint* index, uint* value);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int airbender_mpt_set_slot(void* trie, uint* index, uint* value);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int airbender_mpt_get_account_fields(void* trie, byte* address, uint* fields, byte** value, uint* length);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int airbender_mpt_set_account_fields(void* trie, byte* address, uint* nonce, uint* balance, byte* storageRoot, byte* codeHash);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void* airbender_mpt_open_storage(void* trie, byte* address, int* status);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial void airbender_bloom_set(byte* bloom, byte* master, byte* data, uint length);

    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static unsafe partial int airbender_mpt_nodes(byte** first, byte** last, uint* count);
}
