// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

/// <summary>
/// Bounds of the persisted state window. Implemented by <see cref="IWorldStateManager"/>;
/// each backend (trie / flat) keeps the persisted values co-located with its state DB so
/// wiping the state directory drops the bounds along with the state.
/// </summary>
public interface IStateBoundary
{
    /// <summary>
    /// Absolute lower bound of the persisted state window. Updated when fast/snap sync
    /// completes (= pivot) and after a full pruning run completes (= copied state's block).
    /// Null if never set (archive node syncing from genesis).
    /// </summary>
    long? OldestStateBlock { get; set; }

    /// <summary>
    /// Configured rolling-window retention in blocks (e.g. trie memory pruning). Null when
    /// there is no rolling window (archive, full pruning, flat storage); the absolute floor
    /// is reported via <see cref="OldestStateBlock"/> instead.
    /// </summary>
    long? RetentionWindowBlocks { get; }
}

public interface IWorldStateManager : IStateBoundary
{
    IWorldStateScopeProvider GlobalWorldState { get; }
    IStateReader GlobalStateReader { get; }
    ISnapServer SnapServer { get; }
    IReadOnlyKeyValueStore? HashServer { get; }

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
