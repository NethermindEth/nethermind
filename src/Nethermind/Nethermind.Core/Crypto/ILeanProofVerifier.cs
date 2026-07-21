// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Crypto;

/// <summary>
/// Verifier seam for the EIP-8288 Lean Ethereum primitives: individual leanSPHINCS signatures and
/// leanSTARK proofs, and the recursive STARK that aggregates them. Shared by block validity, mempool
/// wrapper validation, and FOCIL validation.
/// </summary>
/// <remarks>
/// EIP8288-DEVIATION: the actual cryptography requires the Lean Ethereum tooling, which has no C#
/// implementation and whose <c>AGGREGATED_VK</c> and proof formats are still <c>TBD</c> in the spec.
/// The prototype ships a structural stub; a real verifier belongs in a dedicated module with FFI
/// bindings to the Lean Ethereum backend, consumed through this interface.
/// </remarks>
public interface ILeanProofVerifier
{
    /// <summary>Verifies a leanSPHINCS signature over <paramref name="dataHash"/> under <paramref name="verificationKey"/>.</summary>
    bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness);

    /// <summary>Verifies a leanSTARK proof for public-inputs hash <paramref name="dataHash"/> under <paramref name="verificationKey"/>.</summary>
    bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness);

    /// <summary>Verifies a recursive STARK for dependency commitment <paramref name="depsHash"/> under the aggregated verification key.</summary>
    bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof);
}
