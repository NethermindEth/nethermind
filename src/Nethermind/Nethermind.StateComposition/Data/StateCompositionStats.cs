// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition.Data;

public readonly record struct StateCompositionStats
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

    /// <summary>
    /// Aggregate on-chain contract bytecode size, deduplicated by codeHash.
    /// A contract that shares bytecode with another contract (proxy, clone)
    /// contributes 0 bytes on the second observation.
    /// </summary>
    public long CodeBytesTotal { get; init; }

    /// <summary>
    /// Log-bucketed histogram of per-contract storage slot counts.
    /// Bucket index i holds the number of contracts where
    /// <c>min(15, floor(log2(slotCount + 1))) == i</c>.
    /// Invariant: <c>sum(SlotCountHistogram) == ContractsWithStorage</c>.
    /// Always length 16 when set.
    /// </summary>
    public ImmutableArray<long> SlotCountHistogram { get; init; }

    public ImmutableArray<TopContractEntry> TopContractsByDepth { get; init; }
    public ImmutableArray<TopContractEntry> TopContractsByNodes { get; init; }
    public ImmutableArray<TopContractEntry> TopContractsByValueNodes { get; init; }
    /// <summary>
    /// Top contracts ranked by total storage trie byte size.
    /// Nethermind extension — not present in Geth's inspect-trie output.
    /// </summary>
    public ImmutableArray<TopContractEntry> TopContractsBySize { get; init; }
}
