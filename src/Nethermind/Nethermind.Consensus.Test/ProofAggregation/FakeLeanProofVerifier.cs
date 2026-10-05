// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Test.ProofAggregation;

/// <summary>Test verifier with a fixed verdict, to exercise both accept and reject paths.</summary>
internal sealed class FakeLeanProofVerifier(bool result) : ILeanProofVerifier
{
    public void EnsureAvailable() { }

    public int LargestRecursiveInput { get; private set; }
    public int ProofCalls { get; private set; }
    public int VerificationCalls { get; private set; }
    public Action? OnVerification { get; set; }
    public Action? OnProving { get; set; }

    private bool Verify()
    {
        VerificationCalls++;
        OnVerification?.Invoke();
        return result;
    }
    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => Verify();
    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => Verify();
    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => Verify();
    public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
    {
        LargestRecursiveInput = Math.Max(LargestRecursiveInput, input.RecursiveProofs.Count);
        ProofCalls++;
        OnProving?.Invoke();
        return [1];
    }
}
