// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Crypto;

/// <summary>A recursive proof to be folded in: its claimed inner dependencies and the STARK proving them.</summary>
public readonly struct RecursiveProofInput(IReadOnlyList<FrameDependency> innerDeps, byte[] proof)
{
    public IReadOnlyList<FrameDependency> InnerDeps { get; } = innerDeps;
    public byte[] Proof { get; } = proof;
}

/// <summary>Inputs to a recursive aggregation step (spec "private inputs").</summary>
public sealed class AggregationInput
{
    public IReadOnlyList<FrameDependency> Deps { get; init; } = [];
    public IReadOnlyList<byte[]> Witnesses { get; init; } = [];
    public IReadOnlyList<RecursiveProofInput> RecursiveProofs { get; init; } = [];
    public IReadOnlyList<FrameDependency> Discards { get; init; } = [];
}
