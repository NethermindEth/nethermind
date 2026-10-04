// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Crypto;

/// <summary>Public count and output-capacity accounting for one mixed recursive proof.</summary>
public static class LeanProofCapacity
{
    /// <summary>Checks the distinct required claims against the prototype proof bounds.</summary>
    public static string? CapacityError(IReadOnlySet<FrameDependency> required)
        => CreateAppendBudget(required).CapacityError([]);

    /// <summary>Checks additional distinct claims without rescanning the base set.</summary>
    public static string? CapacityError(IReadOnlySet<FrameDependency> existing, int genericProofs, IReadOnlyList<FrameDependency> appended)
    {
        int count = existing.Count;
        HashSet<FrameDependency> added = [];
        foreach (FrameDependency dependency in appended)
        {
            if (existing.Contains(dependency) || !added.Add(dependency)) continue;
            count++;
            string? error = Add(dependency, ref genericProofs);
            if (error is not null) return error;
        }
        return Check(count, genericProofs);
    }

    /// <summary>Measures an unchanged base set once for repeated candidate appendability checks.</summary>
    public static AppendBudget CreateAppendBudget(IReadOnlySet<FrameDependency> existing) => new(existing);

    /// <summary>Capacity for candidates appended independently to an unchanged dependency set.</summary>
    public sealed class AppendBudget
    {
        private readonly IReadOnlySet<FrameDependency> _existing;
        private readonly int _generic;
        private readonly string? _error;

        internal AppendBudget(IReadOnlySet<FrameDependency> existing)
        {
            _existing = existing;
            int generic = 0;
            foreach (FrameDependency dependency in existing) _error ??= Add(dependency, ref generic);
            _generic = generic;
            _error ??= Check(existing.Count, generic);
        }

        /// <summary>Checks one candidate without copying or rescanning the base set.</summary>
        public string? CapacityError(IReadOnlyList<FrameDependency> appended)
        {
            if (_error is not null) return _error;
            return LeanProofCapacity.CapacityError(_existing, _generic, appended);
        }
    }

    private static string? Add(FrameDependency dependency, ref int generic)
    {
        if (dependency.Scheme == Eip8288Constants.LeanStarkScheme) generic++;
        else if (dependency.Scheme != Eip8288Constants.LeanSphincsScheme) return "Unknown dependency proof scheme";
        return null;
    }

    private static string? Check(int count, int generic)
    {
        if (count > Eip8288Constants.MaxProofDependencies) return "Dependency proof count limit exceeded";
        if (generic > Eip8288Constants.MaxGenericStarkProofs) return "Generic STARK proof count limit exceeded";
        long reserved = 12 + (long)count * Eip8288Constants.DependencyTripleLength
            + (count == 0 ? 0 : Eip8288Constants.MaxMixedGuestProofBytes);
        return reserved > Eip8288Constants.MaxProofBytes ? "Dependency proof output limit exceeded" : null;
    }
}
