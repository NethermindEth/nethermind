// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Crypto;

/// <summary>Public claim-count accounting for one mixed recursive proof under the prototype native proof envelope.</summary>
/// <remarks>What this node can prove and verify, not what consensus allows: EIP-8288 sets no block dependency quota, so
/// exceeding this envelope is a local refusal and never excuses an inclusion-list omission.</remarks>
public static class LeanProofCapacity
{
    /// <summary>Checks the distinct required claims against the prototype proof bounds.</summary>
    public static string? CapacityError(IReadOnlySet<FrameDependency> required) => CapacityError(new HashSet<FrameDependency>(), 0, [.. required]);

    /// <summary>Checks additional distinct claims without rescanning the base set.</summary>
    /// <param name="existing">Claims already counted.</param>
    /// <param name="genericProofs">The leanSTARK claims among <paramref name="existing"/>.</param>
    /// <param name="appended">Claims to add; repeats and claims already in <paramref name="existing"/> count once.</param>
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

    private static string? Add(FrameDependency dependency, ref int generic)
    {
        if (!Eip8288Dependencies.IsAcceptedScheme(dependency.Scheme)) return "Unknown dependency proof scheme";
        if (dependency.Scheme == Eip8288Constants.LeanStarkScheme) generic++;
        return null;
    }

    private static string? Check(int count, int generic)
    {
        if (count > Eip8288Constants.MaxProofDependencies) return "Dependency proof count limit exceeded";
        if (generic > Eip8288Constants.MaxGenericStarkProofs) return "Generic STARK proof count limit exceeded";
        return null;
    }
}
