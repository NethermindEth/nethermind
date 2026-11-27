// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Test.ProofAggregation;

/// <summary>A Lean backend whose proof of a statement is its dependency digest, padded to <see cref="ProofLength"/>, so a
/// proof verifies only against the statement it was produced for.</summary>
/// <remarks>Like the native backend, it rejects aggregate proofs under any key but its pinned guest's.</remarks>
internal sealed class FakeLeanProofVerifier : ILeanProofVerifier
{
    private int _proofCalls;

    public int ProofLength { get; set; } = ValueHash256.MemorySize;
    public bool Reject { get; set; }
    /// <summary>Fails verification instead of reaching a verdict, as a native fault does.</summary>
    public bool Fault { get; set; }
    public int ProofCalls => Volatile.Read(ref _proofCalls);
    public int LargestRawInput { get; private set; }
    public ManualResetEventSlim? ProvingGate { get; set; }

    public void EnsureAvailable() { }

    public bool VerifyLeanSphincs(in ValueHash256 message, in ValueHash256 publicKey, ReadOnlySpan<byte> witness) => !Reject;

    public bool VerifyDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) =>
        Fault ? throw new InvalidOperationException("native fault") : !Reject && aggregatedVk.SequenceEqual(Eip8288Constants.AggregatedVk) && proof.SequenceEqual(ProofOf(depsHash));

    public byte[] ProveDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
    {
        ProvingGate?.Wait();
        Interlocked.Increment(ref _proofCalls);
        LargestRawInput = Math.Max(LargestRawInput, input.Deps.Count);
        return ProofOf(depsHash);
    }

    private byte[] ProofOf(in ValueHash256 depsHash)
    {
        byte[] proof = new byte[ProofLength];
        depsHash.Bytes[..Math.Min(ProofLength, ValueHash256.MemorySize)].CopyTo(proof);
        return proof;
    }
}
