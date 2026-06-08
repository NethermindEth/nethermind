// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

/// <summary>
/// Process-wide kill switch for <c>PersistentStorageProvider.PerContractState.CreateStorageTree</c>'s
/// "empty trie → short-circuit reads to zero" optimization. Block-STM sets
/// <see cref="Disabled"/> at DI bootstrap: its <c>MultiVersionMemory</c> overlay can
/// carry pre-block system-contract writes (EIP-4788 BeaconRoots, EIP-2935 blockhash) for
/// slots of an otherwise-empty trie, and the short-circuit would mask them, sending
/// every per-tx SLOAD on the affected contract to <c>ZeroBytes</c>.
/// </summary>
public static class EmptyStorageTreeShortCircuit
{
    // volatile: written once during single-threaded DI bootstrap (BlockStmModule) and read
    // from many worker threads thereafter. Thread-creation already publishes the value via
    // an implicit fence, but the explicit volatile makes the publish-once-then-read-only
    // contract visible to the reader.
    public static volatile bool Disabled;
}
