// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// Determines whether the consensus layer should override its selected builder payload.
/// </summary>
public interface IBuilderOverridePolicy
{
    bool ShouldOverrideBuilder(Block block);
}

/// <summary>
/// Requests a builder override when any registered policy requests one.
/// </summary>
public class CompositeBuilderOverridePolicy(params IBuilderOverridePolicy[] policies) : IBuilderOverridePolicy
{
    /// <inheritdoc />
    public bool ShouldOverrideBuilder(Block block)
    {
        foreach (IBuilderOverridePolicy policy in policies)
        {
            if (policy.ShouldOverrideBuilder(block)) return true;
        }

        return false;
    }
}
