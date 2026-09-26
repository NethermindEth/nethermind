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
    ISnapStateServer SnapStateServer { get; }

    /// <summary>
    /// Used by read only tasks that need to execute blocks.
    /// </summary>
    /// <returns></returns>
    IWorldStateScopeProvider CreateResettableWorldState();

    IOverridableWorldScope CreateOverridableWorldScope();

    /// <summary>
    /// Creates a read-only <see cref="IReadOnlyTrieStore"/> for trie-based operations (e.g. witness generation).
    /// For flat state, returns an adapter over the flat database's trie node data.
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

    /// <summary>
    /// Drop cached state that is not on the ancestry of <paramref name="head"/>: every other branch, and
    /// everything above the head on its own branch, is removed and can no longer be processed from.
    /// Called when the head is force-reset (<c>debug_resetHead</c>).
    /// </summary>
    void DropStateNotReachableFrom(BlockHeader head);
}

public interface IOverridableWorldScope : IDisposable
{
    IWorldStateScopeProvider WorldState { get; }
    IStateReader GlobalStateReader { get; }
    void ResetOverrides();
}
