// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Zkvm.Abstractions;

public static partial class Accelerators
{
    /// <summary>Executes an Airbender u256 delegation and returns its status word.</summary>
    /// <param name="left">The left operand and result buffer.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="op">The delegation's operation mask.</param>
    /// <remarks>Unchecked: operands must be non-aliasing, 32-byte aligned RAM buffers of eight little-endian
    /// 32-bit limbs. The native entry-point name is recognized by bflat's JIT and lowered to a CSR instruction.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe uint AirbenderUInt256Op(void* left, void* right, uint op) => airbender_u256_op(left, right, op);

    /// <summary>Computes Keccak-256 using Airbender's native sponge.</summary>
    /// <param name="data">The input bytes.</param>
    /// <param name="length">The input length.</param>
    /// <param name="output">The 32-byte digest buffer.</param>
    /// <remarks>Unchecked: the aligned four-byte words covering the input must be readable, including
    /// bytes outside the logical input at either end. The output must hold 32 bytes.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void AirbenderKeccak256(byte* data, nuint length, byte* output) => airbender_keccak256(data, length, output);

    /// <summary>Computes Keccak-256 of input stored with each four-byte group reversed.</summary>
    /// <param name="data">The swizzled input, starting at a four-byte group boundary.</param>
    /// <param name="length">The logical input length.</param>
    /// <param name="output">The 32-byte digest buffer.</param>
    /// <remarks>Unchecked: the input must be four-byte aligned and readable through its last whole group,
    /// including padding when the length is not divisible by four.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void AirbenderKeccak256LaidOut(byte* data, nuint length, byte* output) => airbender_keccak256_laid_out(data, length, output);

    /// <summary>Permutes a Keccak-f[1600] state in place using Airbender's delegation.</summary>
    /// <param name="state">The 25-lane state.</param>
    /// <remarks>Unchecked: the state must lie in a 256-byte aligned, 256-byte RAM slot.
    /// The seven lanes after the state are overwritten as delegation scratch.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void AirbenderKeccakFInPlace(ulong* state) => airbender_keccak_f1600_in_place(state);

    /// <summary>The division hints libairbender read after the trusted input: <paramref name="count"/> pairs of a
    /// 32-byte quotient and a 32-byte remainder, little-endian, in 32-byte aligned memory.</summary>
    public static unsafe void AirbenderDivisionHints(out byte* start, out uint count)
    {
        byte* s = null;
        uint n = 0;
        airbender_division_hints(&s, &n);
        start = s;
        count = n;
    }

    /// <summary>Whether libairbender's tries serve the <paramref name="count"/> witness nodes from
    /// <paramref name="first"/> to <paramref name="last"/>: the ones it hashed while it read the trusted input.</summary>
    public static unsafe bool AirbenderMptAttach(byte* first, byte* last, uint count) => airbender_mpt_attach(first, last, count) != 0;

    /// <summary>Whether <paramref name="root"/>, 32 bytes, is the empty tree's root or the hash of a witness node.</summary>
    public static unsafe bool AirbenderMptHasRoot(byte* root) => airbender_mpt_has_root(root) != 0;

    /// <summary>Opens the trie whose root has the 32-byte hash at <paramref name="root"/>.</summary>
    public static unsafe void* AirbenderMptOpen(byte* root) => airbender_mpt_open(root);

    /// <summary>Empties <paramref name="trie"/>.</summary>
    public static unsafe void AirbenderMptClear(void* trie) => airbender_mpt_clear(trie);

    /// <summary>Writes the root hash of <paramref name="trie"/>; negative when a node it needs is missing or malformed.</summary>
    public static unsafe int AirbenderMptRoot(void* trie, byte* output) => airbender_mpt_root(trie, output);

    /// <summary>Finds the account at the 20-byte <paramref name="address"/>: 0 with its encoding where it lies, 1
    /// when absent, negative when a witness node is missing or malformed.</summary>
    public static unsafe int AirbenderMptGetAccount(void* trie, byte* address, byte** value, uint* length) =>
        airbender_mpt_get_account(trie, address, value, length);

    /// <summary>Sets the account to the <paramref name="length"/> bytes at <paramref name="value"/>, or removes it when
    /// the length is 0.</summary>
    public static unsafe int AirbenderMptSetAccount(void* trie, byte* address, byte* value, uint length) =>
        airbender_mpt_set_account(trie, address, value, length);

    /// <summary>Finds the account at the 20-byte <paramref name="address"/> and splits it into
    /// <paramref name="fields"/>, 27 words: the nonce in two, the balance in eight, the storage root, the code hash,
    /// and a word whose bit 0 says the storage is empty and bit 1 that the code is. 1 when absent; 2, with the
    /// encoding where it lies, when it is not four such items; negative when a witness node is missing or
    /// malformed.</summary>
    public static unsafe int AirbenderMptGetAccountFields(void* trie, byte* address, uint* fields, byte** value, uint* length) =>
        airbender_mpt_get_account_fields(trie, address, fields, value, length);

    /// <summary>Sets the account from its nonce (two words), balance (eight words), 32-byte storage root and
    /// 32-byte code hash.</summary>
    public static unsafe int AirbenderMptSetAccountFields(void* trie, byte* address, uint* nonce, uint* balance, byte* storageRoot, byte* codeHash) =>
        airbender_mpt_set_account_fields(trie, address, nonce, balance, storageRoot, codeHash);

    /// <summary>Opens the storage trie of the account at <paramref name="address"/>, empty when there is none. Null
    /// with a negative <paramref name="status"/> when a witness node is missing or malformed, or 2 when the account
    /// is not encoded as usual.</summary>
    public static unsafe void* AirbenderMptOpenStorage(void* trie, byte* address, int* status) =>
        airbender_mpt_open_storage(trie, address, status);

    /// <summary>Sets in the 256-byte <paramref name="bloom"/>, and in <paramref name="master"/> unless null, the
    /// three bits the keccak of the 20 or 32 bytes at <paramref name="data"/> picks.</summary>
    /// <remarks>The native routine reads 32 bytes from <paramref name="data"/> for either length.</remarks>
    public static unsafe void AirbenderBloomSet(byte* bloom, byte* master, byte* data, uint length) =>
        airbender_bloom_set(bloom, master, data, length);

    /// <summary>The state nodes libairbender hashed while it read the trusted input and keeps the tries of: the data
    /// of the first and of the last, and their count. <see langword="false"/> when it read none.</summary>
    public static unsafe bool AirbenderMptNodes(byte** first, byte** last, uint* count) => airbender_mpt_nodes(first, last, count) != 0;

    /// <summary>Reads a storage slot, index and value as eight little-endian words; zero when absent.</summary>
    public static unsafe int AirbenderMptGetSlot(void* trie, uint* index, uint* value) => airbender_mpt_get_slot(trie, index, value);

    /// <summary>Writes a storage slot, index and value as eight little-endian words; zero removes it.</summary>
    public static unsafe int AirbenderMptSetSlot(void* trie, uint* index, uint* value) => airbender_mpt_set_slot(trie, index, value);

    /// <summary>Writes the digest of the code at <paramref name="data"/> into <paramref name="output"/> if it is the
    /// <paramref name="index"/>-th code libairbender hashed while it read the trusted input.</summary>
    public static unsafe bool AirbenderPrehashedCode(byte* data, uint index, byte* output) =>
        airbender_prehashed_code(data, index, output) != 0;

    /// <summary>Recovers a batch of secp256k1 public keys and reports each recovery's success.</summary>
    /// <param name="messages">The 32-byte message hashes.</param>
    /// <param name="signatures">The 64-byte r || s signatures, big-endian.</param>
    /// <param name="recoveryIds">One recovery ID per signature.</param>
    /// <param name="keys">The output 64-byte x || y public keys, big-endian.</param>
    /// <param name="recovered">One output byte per signature: 1 on success, 0 on failure.</param>
    /// <param name="count">The number of signatures.</param>
    /// <remarks>Unchecked: each buffer must hold <paramref name="count"/> entries of its specified size.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void AirbenderSecP256k1RecoverBatch(byte* messages, byte* signatures, byte* recoveryIds,
        byte* keys, byte* recovered, uint count) => airbender_secp256k1_ecrecover_batch(messages, signatures, recoveryIds, keys, recovered, count);

    /// <summary>Checks a batch of hinted secp256k1 recoveries.</summary>
    /// <param name="messages">The 32-byte message hashes.</param>
    /// <param name="signatures">The 64-byte r || s signatures, big-endian.</param>
    /// <param name="recoveryIds">One recovery ID per signature.</param>
    /// <param name="keys">The claimed 64-byte x || y public keys, big-endian.</param>
    /// <param name="ys">The 32-byte y coordinates of the signatures' R points, big-endian.</param>
    /// <param name="weights">The 16-byte random little-endian weights drawn after the hints are fixed.</param>
    /// <param name="count">The number of signatures.</param>
    /// <returns>Whether every hinted recovery passed the batch check.</returns>
    /// <remarks>Unchecked: each buffer must hold <paramref name="count"/> entries of its specified size.
    /// The batch check has a false-acceptance probability of approximately 2^-127.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe bool AirbenderSecP256k1VerifyBatch(byte* messages, byte* signatures, byte* recoveryIds,
        byte* keys, byte* ys, byte* weights, uint count) => airbender_secp256k1_verify_batch(messages, signatures, recoveryIds, keys, ys, weights, count) != 0;
}
