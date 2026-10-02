// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Test.Eip8288;

/// <summary>Test verifier with a fixed verdict, to exercise both accept and reject paths.</summary>
internal sealed class FakeLeanProofVerifier(bool result) : ILeanProofVerifier
{
    public int LargestRecursiveInput { get; private set; }
    public int ProofCalls { get; private set; }
    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => result;
    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => result;
    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => result;
    public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
    {
        LargestRecursiveInput = Math.Max(LargestRecursiveInput, input.RecursiveProofs.Count);
        ProofCalls++;
        return [1];
    }
}
