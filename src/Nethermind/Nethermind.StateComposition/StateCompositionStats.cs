// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition;

public readonly record struct StateCompositionStats()
{
    public long BlockNumber { get; init; }
    public Hash256? StateRoot { get; init; }
    public long AccountsTotal { get; init; }
    public long ContractsTotal { get; init; }
    public long ContractsWithStorage { get; init; }
    public long StorageSlotsTotal { get; init; }
    public long EmptyAccounts { get; init; }
    public long AccountTrieNodeBytes { get; init; }
    public long StorageTrieNodeBytes { get; init; }
    public long AccountTrieFullNodes { get; init; }
    public long AccountTrieShortNodes { get; init; }
    public long AccountTrieValueNodes { get; init; }
    public long StorageTrieFullNodes { get; init; }
    public long StorageTrieShortNodes { get; init; }
    public long StorageTrieValueNodes { get; init; }

    public ImmutableArray<TopContractEntry> TopContractsByDepth { get; init; } = ImmutableArray<TopContractEntry>.Empty;
    public ImmutableArray<TopContractEntry> TopContractsByNodes { get; init; } = ImmutableArray<TopContractEntry>.Empty;
    public ImmutableArray<TopContractEntry> TopContractsByValueNodes { get; init; } = ImmutableArray<TopContractEntry>.Empty;
    /// <summary>
    /// Top contracts ranked by total storage trie byte size.
    /// Nethermind extension — not present in Geth's inspect-trie output.
    /// </summary>
    public ImmutableArray<TopContractEntry> TopContractsBySize { get; init; } = ImmutableArray<TopContractEntry>.Empty;
}
