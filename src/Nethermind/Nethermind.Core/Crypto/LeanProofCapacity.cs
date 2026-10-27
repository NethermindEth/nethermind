// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Crypto;

/// <summary>Public claim-count accounting for one recursive proof under the pinned native backend's bounds.</summary>
/// <remarks>What this node can prove and verify, not what consensus allows: EIP-8288 sets no block dependency quota, so
/// exceeding this envelope is a local refusal and never excuses an inclusion-list omission.</remarks>
public static class LeanProofCapacity
{
    /// <summary>Checks the distinct required claims against the pinned proof bounds.</summary>
    public static string? CapacityError(IReadOnlySet<FrameDependency> required) => CapacityError(new HashSet<FrameDependency>(), [.. required]);

    /// <summary>Checks additional distinct claims without rescanning the base set.</summary>
    /// <param name="existing">Claims already counted.</param>
    /// <param name="appended">Claims to add; repeats and claims already in <paramref name="existing"/> count once.</param>
    public static string? CapacityError(IReadOnlySet<FrameDependency> existing, IReadOnlyList<FrameDependency> appended)
    {
        int count = existing.Count;
        HashSet<FrameDependency> added = [];
        foreach (FrameDependency dependency in appended)
        {
            if (existing.Contains(dependency) || !added.Add(dependency)) continue;
            if (!Eip8288Dependencies.IsAcceptedScheme(dependency.Scheme)) return "Unknown dependency proof scheme";
            count++;
        }
        return count > Eip8288Constants.MaxProofDependencies ? "Dependency proof count limit exceeded" : null;
    }
}
