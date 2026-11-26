// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.BeaconChain.Crypto;

/// <summary>Verifies with <see cref="BlsSigner"/> and <see cref="BatchSignatureVerifier"/>.</summary>
public sealed class BlsSignatureVerifier : ISignatureVerifier
{
    public static BlsSignatureVerifier Instance { get; } = new();

    private BlsSignatureVerifier() { }

    public bool Verify(G1Affine publicKey, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message) =>
        BlsSigner.Verify(publicKey, signature, message);

    public bool VerifyBatch(IReadOnlyList<BlsSignatureSet> sets) => BatchSignatureVerifier.VerifyBatch(sets);

    public int FindInvalid(IReadOnlyList<BlsSignatureSet> sets) => BatchSignatureVerifier.FindInvalid(sets);
}
