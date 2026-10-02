// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Eip8288;

/// <summary>Shares one exact proof verdict across a request's gas-dimension checks.</summary>
public sealed class InclusionListProofVerifier(ILeanProofVerifier verifier) : ILeanProofVerifier
{
    private ValueHash256 _depsHash;
    private byte[]? _verificationKey;
    private byte[]? _proof;
    private bool _valid;

    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        if (_proof is not null && depsHash == _depsHash && aggregatedVk.SequenceEqual(_verificationKey) && proof.SequenceEqual(_proof)) return _valid;
        _valid = verifier.VerifyRecursiveStark(in depsHash, aggregatedVk, proof);
        _depsHash = depsHash;
        _verificationKey = aggregatedVk.ToArray();
        _proof = proof.ToArray();
        return _valid;
    }

    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
        => verifier.VerifyLeanSphincs(in dataHash, in verificationKey, witness);

    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
        => verifier.VerifyLeanStark(in dataHash, in verificationKey, witness);

    public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        => verifier.ProveRecursiveStark(in depsHash, aggregatedVk, input);
}
