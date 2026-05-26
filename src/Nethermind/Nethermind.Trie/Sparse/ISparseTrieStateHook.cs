// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// Receives non-destructive state change deltas from WorldState.Commit(commitRoots: false).
/// Injected into WorldState (concrete class only — IWorldState is not modified).
/// The sparse trie task consumes these deltas to build the state root in the background.
/// </summary>
public interface ISparseTrieStateHook
{
    void OnCommittedDelta(
        StateChangeSource source,
        IReadOnlyDictionary<AddressAsKey, AccountChangeTrace> accountDelta,
        IReadOnlyDictionary<AddressAsKey, IReadOnlyDictionary<UInt256, StorageChangeTrace>> storageDelta);

    void OnFinished();
}

public enum StateChangeSource : byte
{
    SystemPreTx = 0,
    Transaction = 1,
    Withdrawal = 2,
    Reward = 3,
    ExecutionRequest = 4,
}

/// <summary>Before/after account state for a single address in a single commit.</summary>
public readonly record struct AccountChangeTrace(Account? Before, Account? After);

/// <summary>Before/after storage value for a single slot in a single commit.</summary>
public readonly record struct StorageChangeTrace(byte[] Before, byte[] After);
