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
    public static bool Disabled;
}
