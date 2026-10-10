// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Crypto;

/// <summary>Public claim-count accounting for one mixed recursive proof.</summary>
public static class LeanProofCapacity
{
    /// <summary>Distinct dependency and leanSTARK dependency bounds.</summary>
    public sealed record Limits(int Dependencies, int LeanStark);

    /// <summary>The prototype native proof envelope: what this node can prove, not what consensus allows.</summary>
    public static readonly Limits Proof = new(Eip8288Constants.MaxProofDependencies, Eip8288Constants.MaxGenericStarkProofs);

    /// <summary>EIP-8288 block capacity, the only capacity that excuses omitting an inclusion-list transaction.</summary>
    public static readonly Limits Block = new(Eip8288Constants.MaxDepsPerBlock, Eip8288Constants.MaxLeanStarkDepsPerBlock);

    /// <summary>EIP-8288 <c>dependencies_fit_block</c> over a deduplicated dependency set.</summary>
    public static bool FitsBlock(IReadOnlyCollection<FrameDependency> distinct)
    {
        if (distinct.Count > Block.Dependencies) return false;
        int leanStark = 0;
        foreach (FrameDependency dependency in distinct)
            if (dependency.Scheme == Eip8288Constants.LeanStarkScheme && ++leanStark > Block.LeanStark) return false;
        return true;
    }

    /// <summary>Checks the distinct required claims against the prototype proof bounds.</summary>
    public static string? CapacityError(IReadOnlySet<FrameDependency> required)
        => CreateAppendBudget(required).CapacityError([]);

    /// <summary>Checks additional distinct claims without rescanning the base set.</summary>
    public static string? CapacityError(IReadOnlySet<FrameDependency> existing, int genericProofs, IReadOnlyList<FrameDependency> appended,
        Limits? limits = null)
    {
        limits ??= Proof;
        int count = existing.Count;
        HashSet<FrameDependency> added = [];
        foreach (FrameDependency dependency in appended)
        {
            if (existing.Contains(dependency) || !added.Add(dependency)) continue;
            count++;
            string? error = Add(dependency, ref genericProofs);
            if (error is not null) return error;
        }
        return Check(count, genericProofs, limits);
    }

    /// <summary>Measures an unchanged base set once for repeated candidate appendability checks.</summary>
    public static AppendBudget CreateAppendBudget(IReadOnlySet<FrameDependency> existing, Limits? limits = null) => new(existing, limits ?? Proof);

    /// <summary>Capacity for candidates appended independently to an unchanged dependency set.</summary>
    public sealed class AppendBudget
    {
        private readonly IReadOnlySet<FrameDependency> _existing;
        private readonly Limits _limits;
        private readonly int _generic;
        private readonly string? _error;

        internal AppendBudget(IReadOnlySet<FrameDependency> existing, Limits limits)
        {
            _existing = existing;
            _limits = limits;
            int generic = 0;
            foreach (FrameDependency dependency in existing) _error ??= Add(dependency, ref generic);
            _generic = generic;
            _error ??= Check(existing.Count, generic, limits);
        }

        /// <summary>Checks one candidate without copying or rescanning the base set.</summary>
        public string? CapacityError(IReadOnlyList<FrameDependency> appended)
        {
            if (_error is not null) return _error;
            return LeanProofCapacity.CapacityError(_existing, _generic, appended, _limits);
        }
    }

    private static string? Add(FrameDependency dependency, ref int generic)
    {
        if (dependency.Scheme == Eip8288Constants.LeanStarkScheme) generic++;
        else if (dependency.Scheme != Eip8288Constants.LeanSphincsScheme) return "Unknown dependency proof scheme";
        return null;
    }

    private static string? Check(int count, int generic, Limits limits)
    {
        if (count > limits.Dependencies) return "Dependency proof count limit exceeded";
        if (generic > limits.LeanStark) return "Generic STARK proof count limit exceeded";
        return null;
    }
}
