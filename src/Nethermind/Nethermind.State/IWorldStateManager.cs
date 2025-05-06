// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.State.Healing;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IWorldStateManager
{
    IWorldState GlobalWorldState { get; }
    IStateReader GlobalStateReader { get; }
    ISnapServer? SnapServer { get; }
    IReadOnlyKeyValueStore? HashServer { get; }

    /// <summary>
    /// Used by read only tasks that need to execute blocks.
    /// </summary>
    /// <returns></returns>
    IWorldState CreateResettableWorldState();

    /// <summary>
    /// Create a read only world state to warm up another world state
    /// </summary>
    /// <param name="forWarmup">Specify a world state to warm up by the returned world state.</param>
    /// <returns></returns>
    IWorldState CreateWorldStateForWarmingUp(IWorldState forWarmup);

    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    // TODO: These two method can be combined
    IOverridableWorldScope CreateOverridableWorldScope();
    IWorldState CreateOverlayWorldState(IKeyValueStoreWithBatching overlayState, IKeyValueStoreWithBatching overlayCode);

    void InitializeNetwork(IPathRecovery pathRecovery);

    /// <summary>
    /// Probably should be called `verifyState` but the name stuck. Run an internal check for the integrity of the state.
    /// Return false if error is found.
    /// </summary>
    /// <param name="stateAtBlock"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken);
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
