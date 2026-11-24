// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.BeaconChain.Test.Crypto;

internal sealed class RejectingSignatureVerifier : ISignatureVerifier
{
    public bool Verify(G1Affine publicKey, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message) => false;

    public bool VerifyBatch(IReadOnlyList<BlsSignatureSet> sets) => sets.Count == 0;

    public int FindInvalid(IReadOnlyList<BlsSignatureSet> sets) => sets.Count == 0 ? -1 : 0;
}
