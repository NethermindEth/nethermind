// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Nethermind.StateComposition.Data;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Service;

/// <summary>
/// Verifies freeze semantics for the two metrics that cannot be maintained
/// incrementally without a refcount map:
/// - <see cref="CumulativeSizeStats.CodeBytesTotal"/>
/// - <see cref="CumulativeSizeStats.SlotCountHistogram"/>
///
/// Both fields are seeded from a full <see cref="StateCompositionStats"/> scan
/// via <see cref="CumulativeSizeStats.FromScanStats"/> and carried forward
/// unchanged through <see cref="CumulativeSizeStats.ApplyDiff"/> until the next
/// scan refreshes them. This matches the "freeze pattern" approved for
/// incremental updates — imprecision between scans is acceptable because a
/// full scan restores correctness.
/// </summary>
[TestFixture]
public class CumulativeSizeStatsTests
{
    private static StateCompositionStats BuildScanStats(
        long codeBytes,
        ImmutableArray<long> slotHistogram) =>
        new()
        {
            BlockNumber = 100,
            AccountsTotal = 10,
            ContractsTotal = 3,
            ContractsWithStorage = 2,
            StorageSlotsTotal = 50,
            AccountTrieFullNodes = 5,
            AccountTrieShortNodes = 7,
            AccountTrieValueNodes = 3,
            AccountTrieNodeBytes = 1024,
            StorageTrieFullNodes = 4,
            StorageTrieShortNodes = 6,
            StorageTrieValueNodes = 2,
            StorageTrieNodeBytes = 512,
            CodeBytesTotal = codeBytes,
            SlotCountHistogram = slotHistogram,
        };

    [Test]
    public void FromScanStats_SeedsCodeBytesAndSlotHistogram()
    {
        ImmutableArray<long> hist = [0, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        StateCompositionStats scan = BuildScanStats(codeBytes: 9_000, slotHistogram: hist);

        CumulativeSizeStats cumulative = CumulativeSizeStats.FromScanStats(scan);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cumulative.CodeBytesTotal, Is.EqualTo(9_000));
            Assert.That(cumulative.SlotCountHistogram, Is.EqualTo(hist),
                "histogram must be carried through FromScanStats by reference-equal ImmutableArray");
        }
    }

    [Test]
    public void ApplyDiff_PreservesCodeBytesAndSlotHistogram()
    {
        // Freeze pattern: incremental diffs do not touch CodeBytesTotal or SlotCountHistogram.
        // They remain at last-scan values until the next full scan.
        ImmutableArray<long> hist = [0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        CumulativeSizeStats baseline = CumulativeSizeStats.FromScanStats(
            BuildScanStats(codeBytes: 12_345, slotHistogram: hist));

        // Non-trivial diff: structural changes across both tries, account and contract churn.
        TrieDiff diff = new(
            AccountsAdded: 2, AccountsRemoved: 1,
            ContractsAdded: 1, ContractsRemoved: 0,
            AccountTrieBranchesAdded: 1, AccountTrieBranchesRemoved: 0,
            AccountTrieExtensionsAdded: 0, AccountTrieExtensionsRemoved: 1,
            AccountTrieLeavesAdded: 2, AccountTrieLeavesRemoved: 1,
            AccountTrieBytesAdded: 300, AccountTrieBytesRemoved: 100,
            StorageTrieBranchesAdded: 0, StorageTrieBranchesRemoved: 0,
            StorageTrieExtensionsAdded: 1, StorageTrieExtensionsRemoved: 0,
            StorageTrieLeavesAdded: 3, StorageTrieLeavesRemoved: 0,
            StorageTrieBytesAdded: 400, StorageTrieBytesRemoved: 0,
            StorageSlotsAdded: 3, StorageSlotsRemoved: 0,
            ContractsWithStorageAdded: 1, ContractsWithStorageRemoved: 0,
            EmptyAccountsAdded: 0, EmptyAccountsRemoved: 0);

        CumulativeSizeStats updated = baseline.ApplyDiff(diff);

        using (Assert.EnterMultipleScope())
        {
            // Core fields updated by diff math
            Assert.That(updated.AccountsTotal, Is.EqualTo(11));
            Assert.That(updated.ContractsTotal, Is.EqualTo(4));
            Assert.That(updated.StorageSlotsTotal, Is.EqualTo(53));
            Assert.That(updated.ContractsWithStorage, Is.EqualTo(3));
            // Freeze fields preserved verbatim
            Assert.That(updated.CodeBytesTotal, Is.EqualTo(12_345),
                "CodeBytesTotal must carry forward unchanged — no incremental refcount");
            Assert.That(updated.SlotCountHistogram, Is.EqualTo(hist),
                "SlotCountHistogram must carry forward unchanged — no incremental re-bucketing");
        }
    }

    [Test]
    public void ApplyDiff_Chained_StillPreservesFreezeFields()
    {
        ImmutableArray<long> hist = [7, 6, 5, 4, 3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        CumulativeSizeStats stats = CumulativeSizeStats.FromScanStats(
            BuildScanStats(codeBytes: 1_000_000, slotHistogram: hist));

        // Apply three distinct diffs in succession.
        for (int i = 0; i < 3; i++)
        {
            TrieDiff d = new(
                1, 0, 0, 0,
                0, 0, 0, 0, 1, 0, 50, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0,
                0, 0, 0, 0);
            stats = stats.ApplyDiff(d);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.AccountsTotal, Is.EqualTo(13));
            Assert.That(stats.CodeBytesTotal, Is.EqualTo(1_000_000),
                "CodeBytesTotal frozen across N chained diffs");
            Assert.That(stats.SlotCountHistogram, Is.EqualTo(hist),
                "SlotCountHistogram frozen across N chained diffs");
        }
    }
}
