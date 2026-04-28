// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;

namespace Nethermind.StateComposition.Data;

/// <summary>
/// Immutable cumulative state composition stats.
/// <see cref="ApplyDiff"/> returns a new instance with exact adds and removes applied.
/// </summary>
public readonly record struct CumulativeTrieStats(
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
    /// Fixed length of <see cref="SlotCountHistogram"/>. Shared between the visitor
    /// (producer) and the snapshot decoder (persistence) so the two cannot drift.
    /// </summary>
    public const int SlotHistogramLength = 16;

    /// <summary>
    /// Aggregate on-chain contract bytecode, deduplicated by codeHash. Seeded
    /// by each full scan from <see cref="StateCompositionStats.CodeBytesTotal"/>
    /// and carried forward unchanged across incremental diffs — refreshed on
    /// the next full scan. Init-only so <see cref="ApplyDiff"/> preserves it
    /// implicitly via a record-<c>with</c> update.
    /// </summary>
    public long CodeBytesTotal { get; init; }

    /// <summary>
    /// Log-bucketed per-contract slot-count histogram. Length 16 when set.
    /// Bucket <c>i</c> counts contracts whose slot count satisfies
    /// <c>min(15, floor(log2(slotCount + 1))) == i</c>. Seeded by each full
    /// scan and carried forward across incremental diffs — refreshed on the
    /// next full scan.
    /// </summary>
    public ImmutableArray<long> SlotCountHistogram { get; init; }

    /// <summary>
    /// Apply exact structural diff with both adds and removes. Returns new immutable instance.
    /// <para>
    /// Uses <c>this with { ... }</c> rather than the positional constructor so that
    /// init-only fields (<see cref="CodeBytesTotal"/>, <see cref="SlotCountHistogram"/>)
    /// carry forward unchanged. These fields drift between full scans by design —
    /// they are refreshed when the next <see cref="StateCompositionVisitor"/> scan
    /// completes and calls <see cref="FromScanStats"/>.
    /// </para>
    /// </summary>
    internal CumulativeTrieStats ApplyDiff(TrieDiff diff) => this with
    {
        AccountsTotal = AccountsTotal + diff.NetAccounts,
        ContractsTotal = ContractsTotal + diff.NetContracts,
        StorageSlotsTotal = StorageSlotsTotal + diff.NetStorageSlots,
        AccountTrieBranches = AccountTrieBranches + diff.AccountTrieBranchesAdded - diff.AccountTrieBranchesRemoved,
        AccountTrieExtensions = AccountTrieExtensions + diff.AccountTrieExtensionsAdded - diff.AccountTrieExtensionsRemoved,
        AccountTrieLeaves = AccountTrieLeaves + diff.AccountTrieLeavesAdded - diff.AccountTrieLeavesRemoved,
        AccountTrieBytes = AccountTrieBytes + diff.NetAccountTrieBytes,
        StorageTrieBranches = StorageTrieBranches + diff.StorageTrieBranchesAdded - diff.StorageTrieBranchesRemoved,
        StorageTrieExtensions = StorageTrieExtensions + diff.StorageTrieExtensionsAdded - diff.StorageTrieExtensionsRemoved,
        StorageTrieLeaves = StorageTrieLeaves + diff.StorageTrieLeavesAdded - diff.StorageTrieLeavesRemoved,
        StorageTrieBytes = StorageTrieBytes + diff.NetStorageTrieBytes,
        ContractsWithStorage = ContractsWithStorage + diff.NetContractsWithStorage,
        EmptyAccounts = EmptyAccounts + diff.NetEmptyAccounts,
    };

    /// <summary>
    /// Initialize from a full scan's <see cref="StateCompositionStats"/>.
    /// Converts Geth vocabulary (FullNode/ShortNode/ValueNode from
    /// <see cref="TrieLevelStat"/>) to standard MPT (Branches/Extensions/Leaves):
    /// Branches = FullNodes, Extensions = ShortNodes − ValueNodes, Leaves = ValueNodes.
    /// </summary>
    internal static CumulativeTrieStats FromScanStats(StateCompositionStats scan) => new(
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
    )
    {
        CodeBytesTotal = scan.CodeBytesTotal,
        SlotCountHistogram = scan.SlotCountHistogram,
    };
}
