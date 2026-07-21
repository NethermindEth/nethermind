// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

/// <summary>
/// Placeholder <see cref="ILeanProofVerifier"/> for the EIP-8288 prototype. Accepts any non-empty
/// witness / proof structurally.
/// </summary>
/// <remarks>
/// EIP8288-DEVIATION: no cryptographic verification is performed. The Lean Ethereum leanSPHINCS /
/// leanSTARK / recursive-STARK backend has no C# implementation and the spec's <c>AGGREGATED_VK</c>
/// and proof formats are <c>TBD</c>; this stub lets the surrounding aggregation, gas, and validity
/// logic run and be tested end-to-end until a real verifier is wired in through the interface.
/// </remarks>
public sealed class StubLeanProofVerifier : ILeanProofVerifier
{
    public static readonly StubLeanProofVerifier Instance = new();

    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => !witness.IsEmpty;

    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => !witness.IsEmpty;

    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => !proof.IsEmpty;
}
