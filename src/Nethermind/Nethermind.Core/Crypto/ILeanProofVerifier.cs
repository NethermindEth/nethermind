// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Crypto;

/// <summary>
/// Verifier seam for the EIP-8288 Lean Ethereum primitives (leanSPHINCS signatures, leanSTARK proofs,
/// and the recursive STARK that aggregates them), shared by block, mempool-wrapper, and FOCIL validation.
/// EIP8288-DEVIATION: no C# Lean Ethereum backend exists and <c>AGGREGATED_VK</c> / proof formats are
/// TBD, so the prototype ships a deterministic placeholder; a real verifier belongs in a dedicated FFI
/// module behind this seam.
/// </summary>
public interface ILeanProofVerifier
{
    /// <summary>Verifies a leanSPHINCS signature over <paramref name="dataHash"/> under <paramref name="verificationKey"/>.</summary>
    bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness);

    /// <summary>Verifies a leanSTARK proof for public-inputs hash <paramref name="dataHash"/> under <paramref name="verificationKey"/>.</summary>
    bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness);

    /// <summary>Verifies a recursive STARK for dependency commitment <paramref name="depsHash"/> under the aggregated verification key.</summary>
    bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof);
}
