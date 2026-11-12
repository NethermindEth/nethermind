// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Crypto;

/// <summary>A recursive proof to be folded in: its claimed inner dependencies and the STARK proving them.</summary>
public readonly struct RecursiveProofInput
{
    public RecursiveProofInput(IReadOnlyList<FrameDependency> innerDeps, byte[] proof)
    {
        ArgumentNullException.ThrowIfNull(innerDeps);
        ArgumentNullException.ThrowIfNull(proof);
        InnerDeps = Array.AsReadOnly<FrameDependency>([.. innerDeps]);
        byte[] snapshot = (byte[])proof.Clone();
        Proof = snapshot;
        ProofHash = ValueKeccak.Compute(snapshot);
    }

    internal RecursiveProofInput(IReadOnlyList<FrameDependency> innerDeps, byte[] proof, ValueHash256 proofHash)
    {
        InnerDeps = innerDeps;
        Proof = proof;
        ProofHash = proofHash;
    }

    public IReadOnlyList<FrameDependency> InnerDeps { get; }
    public ReadOnlyMemory<byte> Proof { get; }
    public ValueHash256 ProofHash { get; }
}

/// <summary>An EIP-8288 batch leaf: one leanSTARK proof of <c>get_batch_hash(Claims)</c> under one verification key.</summary>
/// <remarks>Each claim yields the dependency <c>(LEANSTARK_SCHEME, claim, VerificationKeyHash)</c>. Claims are nonempty, strictly
/// increasing and at most <see cref="Eip8288Constants.MaxClaimsPerBatchLeaf"/>; the backend rejects any other list.</remarks>
public sealed record BatchLeafInput(ValueHash256 VerificationKeyHash, IReadOnlyList<ValueHash256> Claims, ReadOnlyMemory<byte> Proof);

/// <summary>Inputs to a recursive aggregation step (spec "private inputs").</summary>
/// <remarks>Direct leanSTARK witnesses, <see cref="BatchLeaves"/> and <see cref="RecursiveProofs"/> together are at most
/// <see cref="Eip8288Constants.MaxInnerProofsPerStep"/>.</remarks>
public sealed class AggregationInput
{
    public IReadOnlyList<FrameDependency> Deps { get; init; } = [];
    public IReadOnlyList<ReadOnlyMemory<byte>> Witnesses { get; init; } = [];
    public IReadOnlyList<BatchLeafInput> BatchLeaves { get; init; } = [];
    public IReadOnlyList<RecursiveProofInput> RecursiveProofs { get; init; } = [];
    public IReadOnlyList<FrameDependency> Discards { get; init; } = [];
}
