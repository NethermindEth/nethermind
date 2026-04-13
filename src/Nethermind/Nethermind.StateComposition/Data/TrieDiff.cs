// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.StateComposition.Diff;

namespace Nethermind.StateComposition.Data;

/// <summary>
/// Exact delta between two state roots. Contains both additions and removals
/// for every metric category. Apply to <see cref="CumulativeSizeStats"/> via
/// <see cref="CumulativeSizeStats.ApplyDiff"/>.
///
/// Content-addressed property: walking old root vs new root gives exact counts
/// of nodes/accounts/slots that exist in one but not the other.
/// </summary>
public readonly record struct TrieDiff(
    // Account-level changes
    int AccountsAdded,
    int AccountsRemoved,
    int ContractsAdded,
    int ContractsRemoved,

    // Account trie structure changes
    int AccountTrieBranchesAdded,
    int AccountTrieBranchesRemoved,
    int AccountTrieExtensionsAdded,
    int AccountTrieExtensionsRemoved,
    int AccountTrieLeavesAdded,
    int AccountTrieLeavesRemoved,
    long AccountTrieBytesAdded,
    long AccountTrieBytesRemoved,

    // Storage trie structure changes (aggregate across all contracts)
    int StorageTrieBranchesAdded,
    int StorageTrieBranchesRemoved,
    int StorageTrieExtensionsAdded,
    int StorageTrieExtensionsRemoved,
    int StorageTrieLeavesAdded,
    int StorageTrieLeavesRemoved,
    long StorageTrieBytesAdded,
    long StorageTrieBytesRemoved,

    // Storage slot changes
    long StorageSlotsAdded,
    long StorageSlotsRemoved,

    // Semantic account state changes (HasStorage / IsTotallyEmpty transitions)
    int ContractsWithStorageAdded,
    int ContractsWithStorageRemoved,
    int EmptyAccountsAdded,
    int EmptyAccountsRemoved,

    // Optional per-depth distribution delta. Null when TrackDepthIncrementally is disabled.
    DepthDelta? DepthDelta = null
)
{
    public int NetAccounts => AccountsAdded - AccountsRemoved;
    public int NetContracts => ContractsAdded - ContractsRemoved;
    public long NetStorageSlots => StorageSlotsAdded - StorageSlotsRemoved;
    public int NetContractsWithStorage => ContractsWithStorageAdded - ContractsWithStorageRemoved;
    public int NetEmptyAccounts => EmptyAccountsAdded - EmptyAccountsRemoved;

    public long NetAccountTrieBytes => AccountTrieBytesAdded - AccountTrieBytesRemoved;
    public long NetStorageTrieBytes => StorageTrieBytesAdded - StorageTrieBytesRemoved;
}
