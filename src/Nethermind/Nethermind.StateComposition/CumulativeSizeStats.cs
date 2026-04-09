// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition;

/// <summary>
/// Immutable cumulative state composition stats.
/// <see cref="ApplyDiff"/> returns a new instance with exact adds and removes applied.
/// Unlike a monotonic Add(delta), this correctly handles both additions and removals
/// from trie modifications — no drift possible.
/// </summary>
public readonly record struct CumulativeSizeStats(
    long AccountsTotal,
    long ContractsTotal,
    long StorageSlotsTotal,
    long AccountTrieBranches,
    long AccountTrieExtensions,
    long AccountTrieLeaves,
    long AccountTrieBytes,
    long StorageTrieBranches,
    long StorageTrieExtensions,
    long StorageTrieLeaves,
    long StorageTrieBytes,
    long ContractsWithStorage,
    long EmptyAccounts
)
{
    /// <summary>
    /// Apply exact diff with both adds and removes. Returns new immutable instance.
    /// Every field is updated precisely — no approximation.
    /// </summary>
    public CumulativeSizeStats ApplyDiff(TrieDiff diff) => new(
        AccountsTotal + diff.NetAccounts,
        ContractsTotal + diff.NetContracts,
        StorageSlotsTotal + diff.NetStorageSlots,
        AccountTrieBranches + diff.AccountTrieBranchesAdded - diff.AccountTrieBranchesRemoved,
        AccountTrieExtensions + diff.AccountTrieExtensionsAdded - diff.AccountTrieExtensionsRemoved,
        AccountTrieLeaves + diff.AccountTrieLeavesAdded - diff.AccountTrieLeavesRemoved,
        AccountTrieBytes + diff.NetAccountTrieBytes,
        StorageTrieBranches + diff.StorageTrieBranchesAdded - diff.StorageTrieBranchesRemoved,
        StorageTrieExtensions + diff.StorageTrieExtensionsAdded - diff.StorageTrieExtensionsRemoved,
        StorageTrieLeaves + diff.StorageTrieLeavesAdded - diff.StorageTrieLeavesRemoved,
        StorageTrieBytes + diff.NetStorageTrieBytes,
        ContractsWithStorage + diff.NetContractsWithStorage,
        EmptyAccounts + diff.NetEmptyAccounts
    );

    /// <summary>
    /// Initialize from a full scan's <see cref="StateCompositionStats"/>.
    /// Maps FullNodes→Branches, ShortNodes→Extensions, ValueNodes→Leaves.
    /// </summary>
    public static CumulativeSizeStats FromScanStats(StateCompositionStats scan) => new(
        scan.AccountsTotal,
        scan.ContractsTotal,
        scan.StorageSlotsTotal,
        scan.AccountTrieFullNodes,
        scan.AccountTrieShortNodes - scan.AccountTrieValueNodes,
        scan.AccountTrieValueNodes,
        scan.AccountTrieNodeBytes,
        scan.StorageTrieFullNodes,
        scan.StorageTrieShortNodes - scan.StorageTrieValueNodes,
        scan.StorageTrieValueNodes,
        scan.StorageTrieNodeBytes,
        scan.ContractsWithStorage,
        scan.EmptyAccounts
    );
}
