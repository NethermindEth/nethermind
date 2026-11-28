// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Consensus.Test.ProofAggregation;

/// <summary>A Lean backend whose proof of a statement is its dependency digest, padded to <see cref="ProofLength"/>, so a
/// proof verifies only against the statement it was produced for.</summary>
/// <remarks>Like the native backend, it rejects aggregate proofs under any key but its pinned guest's.</remarks>
internal sealed class FakeLeanProofVerifier : ILeanProofVerifier, ILeanSphincsVerifier
{
    private int _proofCalls;

    public int ProofLength { get; set; } = ValueHash256.MemorySize;
    public bool Reject { get; set; }
    /// <summary>Fails verification instead of reaching a verdict, as a native fault does.</summary>
    public bool Fault { get; set; }
    public int ProofCalls => Volatile.Read(ref _proofCalls);
    /// <summary>Every input proven, in call order.</summary>
    public List<AggregationInput> ProvingInputs { get; } = [];
    /// <summary>Holds the proving of each statement keyed here until its gate is set.</summary>
    public Dictionary<ValueHash256, ManualResetEventSlim> ProvingGates { get; } = [];

    public void EnsureAvailable() { }

    public bool VerifyLeanSphincs(in ValueHash256 message, in ValueHash256 publicKey, ReadOnlySpan<byte> witness) => !Reject;

    public bool Verify(in ValueHash256 publicKey, in ValueHash256 message, ReadOnlySpan<byte> signature) => !Reject;

    public bool VerifyDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) =>
        Fault ? throw new InvalidOperationException("native fault") : !Reject && aggregatedVk.SequenceEqual(Eip8288Constants.AggregatedVk) && proof.SequenceEqual(ProofOf(depsHash));

    public byte[] ProveDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
    {
        if (Fault) throw new InvalidOperationException("native fault");
        if (ProvingGates.TryGetValue(depsHash, out ManualResetEventSlim? gate)) gate.Wait();
        Interlocked.Increment(ref _proofCalls);
        lock (ProvingInputs) ProvingInputs.Add(input);
        return ProofOf(depsHash);
    }

    private byte[] ProofOf(in ValueHash256 depsHash)
    {
        byte[] proof = new byte[ProofLength];
        depsHash.Bytes[..Math.Min(ProofLength, ValueHash256.MemorySize)].CopyTo(proof);
        return proof;
    }
}
