// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IWorldStateManager
{
    IWorldStateScopeProvider GlobalWorldState { get; }
    IStateReader GlobalStateReader { get; }
    ISnapServer SnapServer { get; }
    IReadOnlyKeyValueStore? HashServer { get; }

    /// <summary>
    /// Configured rolling-window retention in blocks (e.g. trie memory pruning). Null when
    /// there is no rolling window (archive, full pruning, flat storage); the absolute floor is
    /// then reported via <see cref="OldestStateBlock"/>.
    /// </summary>
    long? RetentionWindowBlocks { get; }

    /// <summary>
    /// Absolute lower bound of the persisted state window. Updated when fast/snap sync
    /// completes (= pivot) and after a full pruning run completes (= copied state's block).
    /// Null if never set (archive node syncing from genesis).
    /// </summary>
    long? OldestStateBlock { get; set; }

    /// <summary>
    /// Used by read only tasks that need to execute blocks.
    /// </summary>
    /// <returns></returns>
    IWorldStateScopeProvider CreateResettableWorldState();

    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    IOverridableWorldScope CreateOverridableWorldScope();

    /// <summary>
    /// Creates a read-only <see cref="IReadOnlyTrieStore"/> for trie-based operations (e.g. witness generation).
    /// For trie mode, returns the existing read-only trie store.
    /// For flat mode, returns an adapter over the flat DB's trie node data.
    /// </summary>
    IReadOnlyTrieStore CreateReadOnlyTrieStore();

    /// <summary>
    /// Probably should be called `verifyState` but the name stuck. Run an internal check for the integrity of the state.
    /// Return false if error is found.
    /// </summary>
    /// <param name="stateAtBlock"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken);

    /// <summary>
    /// Persist and clear cache. Used by some tests.
    /// </summary>
    void FlushCache(CancellationToken cancellationToken);
}

public interface IOverridableWorldScope : IDisposable
{
    IWorldStateScopeProvider WorldState { get; }
    IStateReader GlobalStateReader { get; }
    void ResetOverrides();
}
