// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Eip8288;

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

/// <summary>The "verify, union and discard" logic shared by mempool re-forwarders, FOCIL creators, and block builders.</summary>
public static class RecursiveStarkAggregator
{
    /// <summary>
    /// Runs the EIP-8288 recursive STARK statement: verify direct dependencies against their witnesses
    /// and each nested proof against its claimed deps, then union and remove discards/duplicates,
    /// returning the filtered set and its commitment.
    /// EIP8288-ISSUE: discards are matched by triple equality rather than the spec's per-dep hash.
    /// </summary>
    public static bool TryAggregate(AggregationInput input, ILeanProofVerifier verifier, out IReadOnlyList<FrameDependency> filteredDeps, out ValueHash256 depsHash)
    {
        filteredDeps = [];
        depsHash = default;

        if (input.Deps.Count != input.Witnesses.Count) return false;

        List<FrameDependency> allDeps = [];
        for (int i = 0; i < input.Deps.Count; i++)
        {
            FrameDependency dep = input.Deps[i];
            byte[] witness = input.Witnesses[i];
            bool valid = dep.Scheme switch
            {
                Eip8288Constants.LeanSphincsScheme => verifier.VerifyLeanSphincs(dep.DataHash, dep.VerificationKey, witness),
                Eip8288Constants.LeanStarkScheme => verifier.VerifyLeanStark(dep.DataHash, dep.VerificationKey, witness),
                _ => false,
            };
            if (!valid) return false;
            allDeps.Add(dep);
        }

        foreach (RecursiveProofInput recursiveProof in input.RecursiveProofs)
        {
            ValueHash256 innerHash = Eip8288Dependencies.ComputeDepsHash(recursiveProof.InnerDeps);
            if (!verifier.VerifyRecursiveStark(in innerHash, Eip8288Constants.AggregatedVk, recursiveProof.Proof)) return false;
            allDeps.AddRange(recursiveProof.InnerDeps);
        }

        HashSet<FrameDependency> discards = input.Discards.Count == 0 ? [] : [.. input.Discards];

        List<FrameDependency> filtered = [];
        HashSet<FrameDependency> seen = [];
        foreach (FrameDependency dep in allDeps)
        {
            if (!seen.Add(dep)) continue;
            if (discards.Contains(dep)) continue;
            filtered.Add(dep);
        }

        filteredDeps = filtered;
        depsHash = Eip8288Dependencies.ComputeDepsHash(filtered);
        return true;
    }
}
