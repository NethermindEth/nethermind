// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition;

public readonly record struct StateCompositionStats
{
    public long BlockNumber { get; init; }
    public Hash256? StateRoot { get; init; }
    public long AccountsTotal { get; init; }
    public long ContractsTotal { get; init; }
    public long ContractsWithStorage { get; init; }
    public long StorageSlotsTotal { get; init; }
    public long AccountTrieNodeBytes { get; init; }
    public long StorageTrieNodeBytes { get; init; }
    public long AccountTrieBranchNodes { get; init; }
    public long AccountTrieExtensionNodes { get; init; }
    public long AccountTrieLeafNodes { get; init; }
    public long StorageTrieBranchNodes { get; init; }
    public long StorageTrieExtensionNodes { get; init; }
    public long StorageTrieLeafNodes { get; init; }

    // Geth parity: Top-N contract rankings
    public ImmutableArray<TopContractEntry> TopContractsByDepth { get; init; }
    public ImmutableArray<TopContractEntry> TopContractsByNodes { get; init; }
    public ImmutableArray<TopContractEntry> TopContractsBySlots { get; init; }
}
