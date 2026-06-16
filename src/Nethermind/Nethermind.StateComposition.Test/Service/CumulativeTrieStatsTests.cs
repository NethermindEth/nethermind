// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Immutable;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Service;

/// <summary>
/// Verifies freeze semantics for the two metrics that cannot be maintained
/// incrementally without a refcount map:
/// - <see cref="CumulativeTrieStats.CodeBytesTotal"/>
/// - <see cref="CumulativeTrieStats.SlotCountHistogram"/>
///
/// Both fields are seeded from a full <see cref="StateCompositionStats"/> scan
/// via <see cref="CumulativeTrieStats.FromScanStats"/> and carried forward
/// unchanged through <see cref="CumulativeTrieStats.ApplyDiff"/> until the next
/// scan refreshes them. This matches the "freeze pattern" approved for
/// incremental updates — imprecision between scans is acceptable because a
/// full scan restores correctness.
/// </summary>
[TestFixture]
public class CumulativeTrieStatsTests
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

        CumulativeTrieStats cumulative = CumulativeTrieStats.FromScanStats(scan);

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
        ImmutableArray<long> hist = [0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        CumulativeTrieStats baseline = CumulativeTrieStats.FromScanStats(
            BuildScanStats(codeBytes: 12_345, slotHistogram: hist));

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
            EmptyAccountsAdded: 0, EmptyAccountsRemoved: 0,
            DepthDelta: new CumulativeDepthStats(),
            SlotCountChanges: Array.Empty<SlotCountChange>(),
            CodeHashChanges: Array.Empty<CodeHashChange>());

        CumulativeTrieStats updated = baseline.ApplyDiff(diff);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.AccountsTotal, Is.EqualTo(11));
            Assert.That(updated.ContractsTotal, Is.EqualTo(4));
            Assert.That(updated.StorageSlotsTotal, Is.EqualTo(53));
            Assert.That(updated.ContractsWithStorage, Is.EqualTo(3));
            Assert.That(updated.CodeBytesTotal, Is.EqualTo(12_345),
                "CodeBytesTotal must carry forward unchanged — no incremental refcount");
            Assert.That(updated.SlotCountHistogram, Is.EqualTo(hist),
                "SlotCountHistogram must carry forward unchanged — no incremental re-bucketing");
        }
    }

}
