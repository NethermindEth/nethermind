// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.BeaconChain.Crypto;

/// <summary>The BLS signature checks every consensus rule runs, so one implementation decides them all.</summary>
public interface ISignatureVerifier
{
    /// <summary>Verifies one signature over <paramref name="message"/> against <paramref name="publicKey"/>.</summary>
    bool Verify(G1Affine publicKey, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message);

    /// <summary>Verifies every set together; <c>true</c> for an empty list.</summary>
    bool VerifyBatch(IReadOnlyList<BlsSignatureSet> sets);

    /// <summary>Returns the index of the first invalid set, or -1 when every set is valid.</summary>
    int FindInvalid(IReadOnlyList<BlsSignatureSet> sets);
}
