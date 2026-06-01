// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// Receives non-destructive state-change notifications from <c>WorldState.Commit(commitRoots: false)</c>
/// as each commit phase (system pre-tx, per-tx, withdrawals, rewards, execution requests) resolves
/// its journal. The M4 sparse-trie task consumes these to build the state root concurrently with
/// EVM execution instead of synchronously at RecalculateStateRoot.
/// </summary>
/// <remarks>
/// Injected into the concrete <see cref="Nethermind.Core"/> WorldState only; <c>IWorldState</c> is
/// not modified. The hook is called with the committing world state itself so it can pull the
/// just-committed account/storage keys and their current values via the providers' normal read
/// surface — no per-phase dictionary materialization in the hot commit loops. The implementation
/// must treat all reads as a snapshot of "state as of this commit phase" and must not mutate
/// anything. Everything here is a no-op when no hook is attached (the common, sparse-off case).
/// </remarks>
public interface ISparseTrieStateHook
{
    /// <summary>
    /// Called after a <c>Commit(commitRoots: false)</c> phase has resolved its journal into the
    /// block-change buffers. <paramref name="committedAccounts"/> and <paramref name="committedStorage"/>
    /// enumerate exactly the keys this phase touched (no values — the consumer reads current values
    /// through <paramref name="reader"/>), so the consumer can stream leaf updates for them.
    /// </summary>
    void OnCommittedDelta(
        StateChangeSource source,
        IReadOnlyCollection<AddressAsKey> committedAccounts,
        IReadOnlyCollection<(Address Address, UInt256 Slot)> committedStorage,
        ISparseTrieStateReader reader);

    /// <summary>Called once after the final commit phase of the block; signals the task to finish.</summary>
    void OnFinished();
}

/// <summary>
/// Read-only snapshot accessor the hook uses to pull current account/storage values for the keys
/// reported by <see cref="ISparseTrieStateHook.OnCommittedDelta"/>, without coupling the hook to a
/// concrete world-state type.
/// </summary>
public interface ISparseTrieStateReader
{
    /// <summary>Current account (post-this-phase), or null if deleted/absent.</summary>
    Account? GetAccount(Address address);

    /// <summary>Current storage value (post-this-phase) for the cell.</summary>
    ReadOnlySpan<byte> GetStorage(Address address, in UInt256 slot);
}

public enum StateChangeSource : byte
{
    SystemPreTx = 0,
    Transaction = 1,
    Withdrawal = 2,
    Reward = 3,
    ExecutionRequest = 4,
}
