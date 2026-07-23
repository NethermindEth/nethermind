// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

/// <summary>
/// Placeholder <see cref="ILeanProofVerifier"/> for the EIP-8288 prototype. A "proof" is the Keccak of
/// its public inputs, so a correctly generated proof verifies and any tampered one is rejected — enough
/// to exercise the client's accept/reject plumbing through the real code path.
/// </summary>
/// <remarks>
/// EIP8288-DEVIATION: this is NOT cryptography — it provides no soundness whatsoever (a "proof" is
/// public and trivially forgeable). It exists only so the aggregation, gas, and validity logic can be
/// tested end-to-end until a real Lean Ethereum verifier (FFI to the Rust backend) replaces it through
/// this interface, once <c>AGGREGATED_VK</c> and the proof formats are finalized.
/// </remarks>
public sealed class PlaceholderLeanProofVerifier : ILeanProofVerifier
{
    public static readonly PlaceholderLeanProofVerifier Instance = new();

    public static byte[] ProveLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey) =>
        Tag(Eip8288Constants.LeanSphincsScheme, in dataHash, in verificationKey);

    public static byte[] ProveLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey) =>
        Tag(Eip8288Constants.LeanStarkScheme, in dataHash, in verificationKey);

    public static byte[] ProveRecursive(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk)
    {
        Span<byte> buffer = stackalloc byte[32 + aggregatedVk.Length];
        depsHash.Bytes.CopyTo(buffer);
        aggregatedVk.CopyTo(buffer[32..]);
        return ValueKeccak.Compute(buffer).ToByteArray();
    }

    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) =>
        witness.SequenceEqual(ProveLeanSphincs(in dataHash, in verificationKey));

    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) =>
        witness.SequenceEqual(ProveLeanStark(in dataHash, in verificationKey));

    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) =>
        proof.SequenceEqual(ProveRecursive(in depsHash, aggregatedVk));

    private static byte[] Tag(byte scheme, in ValueHash256 dataHash, in ValueHash256 verificationKey)
    {
        Span<byte> buffer = stackalloc byte[65];
        buffer[0] = scheme;
        dataHash.Bytes.CopyTo(buffer[1..33]);
        verificationKey.Bytes.CopyTo(buffer[33..65]);
        return ValueKeccak.Compute(buffer).ToByteArray();
    }
}
