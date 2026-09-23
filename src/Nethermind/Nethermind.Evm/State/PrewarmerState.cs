// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.State;

/// <summary>
/// The prewarmer state of a lifetime scope: the block caches shared with the main execution, and whether this
/// scope is a speculative populator or the main execution that consumes them.
/// </summary>
/// <remarks>
/// Registered once per lifetime scope so components that must behave differently in a speculative env can be
/// wired to the right value instead of probing the world state or its scope provider at run time.
/// </remarks>
public interface IPrewarmerState
{
    PreBlockCaches Caches { get; }

    /// <summary>True for read-only populator envs; false for the read-write main world state.</summary>
    bool IsPrewarmer { get; }
}

/// <inheritdoc cref="IPrewarmerState"/>
public sealed class PrewarmerState(PreBlockCaches caches, bool isPrewarmer) : IPrewarmerState
{
    public PreBlockCaches Caches => caches;
    public bool IsPrewarmer => isPrewarmer;
}
