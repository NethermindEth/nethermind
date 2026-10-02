// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Crypto;

/// <summary>Verifies Lean Ethereum signatures and proofs for dependency validation and aggregation.</summary>
public interface ILeanProofVerifier
{
    /// <summary>Checks that the backend is ready before a configured dependency-proof fork starts.</summary>
    void EnsureAvailable();

    /// <summary>Verifies a leanSPHINCS signature over <paramref name="dataHash"/> under <paramref name="verificationKey"/>.</summary>
    bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness);

    /// <summary>Verifies a leanSTARK proof for public-inputs hash <paramref name="dataHash"/> under <paramref name="verificationKey"/>.</summary>
    bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness);

    /// <summary>Verifies a recursive STARK for dependency commitment <paramref name="depsHash"/> under the aggregated verification key.</summary>
    bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof);

    /// <summary>Produces the recursive STARK a builder attaches to its block.</summary>
    /// <remarks>On the same seam as verification so a node cannot prove and verify with different backends.</remarks>
    byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input);
}
