// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Healing;
using Nethermind.State.Snap;
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
    /// <param name="forWarmup">Specify a world state to warm up by the returned world state.</param>
    /// <returns></returns>
    IWorldState CreateResettableWorldState(IWorldState? forWarmup = null);

    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    // TODO: These two method can be combined
    IOverridableWorldScope CreateOverridableWorldScope();
    IWorldState CreateOverlayWorldState(IKeyValueStoreWithBatching overlayState, IKeyValueStore overlayCode);

    void InitializeNetwork(ITrieNodeRecovery<IReadOnlyList<Hash256>> hashRecovery, ITrieNodeRecovery<GetTrieNodesRequest> nodeRecovery);
    bool TryStartVerifyTrie(BlockHeader stateAtBlock);
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
