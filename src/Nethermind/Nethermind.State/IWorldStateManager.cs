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
    /// Oldest block for which state is available given the chain head, considering only this
    /// manager's rolling-window retention (e.g. trie memory pruning). Returns null when the
    /// implementation has no rolling window (archive, full pruning, flat storage — the
    /// absolute floor is reported separately via <see cref="OldestStateBlock"/>).
    /// </summary>
    long? GetOldestStateBlock(long headBlock);

    /// <summary>
    /// Absolute lower bound of the persisted state window. Updated when fast/snap sync
    /// completes (= pivot) and after a full pruning run completes (= copied state's block).
    /// Null if never set (archive node syncing from genesis).
    /// </summary>
    long? OldestStateBlock { get; set; }

    /// <summary>
    /// Whether this manager can serve trie-based state proofs (e.g. for <c>eth_getProof</c>)
    /// with the same retention guarantees as state queries. Trie-backed implementations return
    /// true; flat-storage implementations return false because proof reconstruction is either
    /// limited to retained flat snapshots or requires walking the full state.
    /// </summary>
    bool SupportsTrieProofs { get; }

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
