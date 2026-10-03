// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Eip8288;

/// <summary>Tracks accepted witnesses without rebuilding the accumulated proof for each candidate.</summary>
internal sealed class LeanProofBudget
{
    private readonly HashSet<FrameDependency> _required = [];
    private readonly HashSet<FrameDependency> _coverage = [];
    private readonly HashSet<ValueHash256> _parents = [];
    private readonly Dictionary<FrameDependency, int> _genericLengths = [];
    private int _genericProofs;
    private AggregationInput? _inclusionListInput;
    private HashSet<FrameDependency>? _inclusionListCoverage;

    public List<FrameDependency> Missing(IReadOnlyList<FrameDependency> required)
    {
        List<FrameDependency> missing = [];
        foreach (FrameDependency dependency in required)
            if (!_coverage.Contains(dependency)) missing.Add(dependency);
        return missing;
    }

    public bool TryUseInclusionList(AggregationInput? verified, IReadOnlyList<FrameDependency> required, out AggregationInput input)
    {
        input = new();
        if (verified is null) return false;
        if (!ReferenceEquals(verified, _inclusionListInput))
        {
            _inclusionListInput = verified;
            _inclusionListCoverage = [.. verified.Deps];
            foreach (RecursiveProofInput parent in verified.RecursiveProofs) _inclusionListCoverage.UnionWith(parent.InnerDeps);
            _inclusionListCoverage.ExceptWith(verified.Discards);
        }
        foreach (FrameDependency dependency in required)
            if (!_inclusionListCoverage!.Contains(dependency)) return false;
        input = verified;
        return true;
    }

    public bool TryPrepare(AggregationInput input, IReadOnlyList<FrameDependency> required,
        out AggregationInput contribution, out string? error)
    {
        contribution = new();
        error = null;
        error = CapacityError(_required, _genericProofs, required);
        if (error is not null) return false;
        HashSet<FrameDependency> addedCoverage = [];
        List<RecursiveProofInput> parents = [];
        HashSet<ValueHash256> addedParents = [];
        foreach (RecursiveProofInput parent in input.RecursiveProofs)
        {
            if (_parents.Contains(parent.ProofHash) || !addedParents.Add(parent.ProofHash)) continue;
            parents.Add(parent);
            foreach (FrameDependency dependency in parent.InnerDeps)
                if (!_coverage.Contains(dependency)) addedCoverage.Add(dependency);
        }
        List<FrameDependency> direct = [];
        List<System.ReadOnlyMemory<byte>> witnesses = [];
        for (int i = 0; i < input.Deps.Count; i++)
        {
            FrameDependency dependency = input.Deps[i];
            if (_coverage.Contains(dependency) || !addedCoverage.Add(dependency)) continue;
            direct.Add(dependency);
            witnesses.Add(input.Witnesses[i]);
        }
        foreach (FrameDependency dependency in required)
            if (!_coverage.Contains(dependency) && !addedCoverage.Contains(dependency))
            { error = "Missing verified dependency witnesses"; return false; }
        contribution = new() { Deps = direct, Witnesses = witnesses, RecursiveProofs = parents };
        Dictionary<FrameDependency, int> lengths = new(_genericLengths);
        if (!AddWitnessLengths(contribution, lengths)) { error = "Invalid verified proof envelope"; return false; }
        HashSet<FrameDependency> union = [.. _required, .. required];
        error = LeanProofCapacity.CapacityError(union, lengths);
        if (error is not null) return false;
        return true;
    }

    private static bool AddWitnessLengths(AggregationInput input, Dictionary<FrameDependency, int> lengths)
    {
        for (int i = 0; i < input.Deps.Count; i++)
            if (input.Deps[i].Scheme == Eip8288Constants.LeanStarkScheme)
                LeanProofCapacity.AddWitnessLength(lengths, input.Deps[i], input.Witnesses[i].Length);
        foreach (RecursiveProofInput parent in input.RecursiveProofs)
        {
            bool generic = false;
            foreach (FrameDependency dependency in parent.InnerDeps)
                if (dependency.Scheme == Eip8288Constants.LeanStarkScheme) { generic = true; break; }
            if (generic && !LeanProofCapacity.TryReadGenericWitnessLengths(parent.Proof.Span, lengths)) return false;
        }
        return true;
    }

    internal static string? CapacityError(IReadOnlySet<FrameDependency> existing, int genericProofs, IReadOnlyList<FrameDependency> appended)
    {
        int count = existing.Count;
        foreach (FrameDependency dependency in appended)
            if (!existing.Contains(dependency))
            {
                count++;
                if (dependency.Scheme == Eip8288Constants.LeanStarkScheme) genericProofs++;
            }
        if (count > Eip8288Constants.MaxProofDependencies) return "Dependency proof count limit exceeded";
        return genericProofs > Eip8288Constants.MaxGenericStarkProofs ? "Generic STARK proof count limit exceeded" : null;
    }

    public void Commit(AggregationInput contribution, IReadOnlyList<FrameDependency> required)
    {
        AddWitnessLengths(contribution, _genericLengths);
        foreach (FrameDependency dependency in required)
            if (_required.Add(dependency) && dependency.Scheme == Eip8288Constants.LeanStarkScheme) _genericProofs++;
        for (int i = 0; i < contribution.Deps.Count; i++)
        {
            _coverage.Add(contribution.Deps[i]);
        }
        foreach (RecursiveProofInput parent in contribution.RecursiveProofs)
        {
            _parents.Add(parent.ProofHash);
            _coverage.UnionWith(parent.InnerDeps);
        }
    }

    public LeanProofBudget Clone()
    {
        LeanProofBudget clone = new()
        {
            _genericProofs = _genericProofs,
            _inclusionListInput = _inclusionListInput,
            _inclusionListCoverage = _inclusionListCoverage
        };
        clone._required.UnionWith(_required);
        clone._coverage.UnionWith(_coverage);
        clone._parents.UnionWith(_parents);
        foreach ((FrameDependency dependency, int length) in _genericLengths) clone._genericLengths.Add(dependency, length);
        return clone;
    }
}
