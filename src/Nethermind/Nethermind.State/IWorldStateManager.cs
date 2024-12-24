// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IWorldStateManager
{
    IWorldState GlobalWorldState { get; }
    IStateReader GlobalStateReader { get; }
    IReadOnlyTrieStore TrieStore { get; }
    bool SupportHashLookup { get; }

    /// <summary>
    /// Used by read only tasks that need to execute blocks.
    /// </summary>
    /// <param name="forWarmup">Specify a world state to warm up by the returned world state.</param>
    /// <returns></returns>
    IWorldState CreateResettableWorldState(IWorldState? forWarmup = null);

    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    IOverridableWorldScope CreateOverridableWorldScope();
}

public interface IOverridableWorldScope
{
    IOverridableWorldState WorldState { get; }
    IStateReader GlobalStateReader { get; }
}

public interface IOverridableWorldState : IWorldState
{
    void ResetOverrides();
}
