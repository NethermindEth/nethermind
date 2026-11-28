// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Crypto;

/// <summary>Verifies and produces the Lean Ethereum proofs of EIP-8288 dependency aggregation.</summary>
public interface ILeanProofVerifier
{
    /// <summary>Checks that the backend is ready before a configured EIP-8288 fork starts.</summary>
    void EnsureAvailable();

    /// <summary>Verifies a leanSPHINCS signature over <paramref name="message"/> under <paramref name="publicKey"/>.</summary>
    /// <param name="witness">The public key followed by the signature, as the native backend takes it.</param>
    bool VerifyLeanSphincs(in ValueHash256 message, in ValueHash256 publicKey, ReadOnlySpan<byte> witness);

    /// <summary>Verifies a recursive STARK for dependency digest <paramref name="depsHash"/> under <paramref name="aggregatedVk"/>.</summary>
    /// <remarks>The native backend accepts only its pinned guest key; it does not verify arbitrary recursive programs.</remarks>
    bool VerifyDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof);

    /// <summary>Produces the recursive STARK a builder attaches to its block.</summary>
    /// <remarks>On the same seam as verification so a node cannot prove and verify with different backends.</remarks>
    /// <exception cref="LeanProverBusyException">The prover cannot take the proof now.</exception>
    byte[] ProveDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input);
}
