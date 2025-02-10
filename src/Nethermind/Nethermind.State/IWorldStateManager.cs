// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IWorldStateManager : IResettableWorldStateManager
{
    IWorldState GlobalWorldState { get; }

    bool SupportHashLookup => false;

    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;
}

public interface IResettableWorldStateManager
{
    IStateReader GlobalStateReader { get; }

    /// <summary>
    /// Used by read only tasks that need to execute blocks.
    /// </summary>
    /// <param name="forWarmup">Specify a world state to warm up by the returned world state.</param>
    /// <returns></returns>
    IWorldState CreateResettableWorldState(IWorldState? forWarmup = null);
}
