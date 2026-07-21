// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

/// <summary>
/// Placeholder <see cref="ILeanProofVerifier"/> for the EIP-8288 prototype: accepts any non-empty
/// witness / proof structurally, performing no cryptographic verification (the Lean Ethereum backend
/// is TBD). Lets the surrounding aggregation, gas, and validity logic run until a real verifier lands.
/// </summary>
public sealed class StubLeanProofVerifier : ILeanProofVerifier
{
    public static readonly StubLeanProofVerifier Instance = new();

    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => !witness.IsEmpty;

    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => !witness.IsEmpty;

    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => !proof.IsEmpty;
}
