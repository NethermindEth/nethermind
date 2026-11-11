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
