// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Immutable;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition.Data;

internal readonly record struct StateCompositionStats
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
    public long CodeBytesTotal { get; init; }
    public ImmutableArray<long> SlotCountHistogram { get; init; }
    public ImmutableArray<TopContractEntry> TopContractsByDepth { get; init; }
    public ImmutableArray<TopContractEntry> TopContractsByNodes { get; init; }
    public ImmutableArray<TopContractEntry> TopContractsByValueNodes { get; init; }
    public ImmutableArray<TopContractEntry> TopContractsBySize { get; init; }

    /// <summary>
    /// Per-contract slot count, keyed by account-hash. Seeds the state holder's
    /// incremental slot tracker so <see cref="SlotCountHistogram"/> can be updated
    /// in-place by subsequent <see cref="TrieDiff"/>s. The visitor hands ownership
    /// of the backing dictionary to the holder, so the concrete type is exposed
    /// to avoid a wrapper allocation at the hand-off.
    /// </summary>
    public Dictionary<ValueHash256, long>? SlotCountByAddress { get; init; }

    /// <summary>
    /// Distinct bytecode sizes observed during the scan, keyed by code hash.
    /// Together with <see cref="CodeHashRefcounts"/> these feed the incremental
    /// <see cref="CodeBytesTotal"/> tracker.
    /// </summary>
    public Dictionary<ValueHash256, int>? CodeHashSizes { get; init; }

    /// <summary>
    /// Per-code-hash reference count — number of accounts whose CodeHash equals
    /// the given hash. Used to detect when the last account referencing a code
    /// hash disappears, so <see cref="CodeBytesTotal"/> can be decremented.
    /// </summary>
    public Dictionary<ValueHash256, int>? CodeHashRefcounts { get; init; }
}
