// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.IndexTables;

public class IndexTableMergerTests
{
    [Test]
    public void Merge_empty_sources_returns_empty()
    {
        List<IndexEntry> result = IndexTableMerger.Merge([]);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Merge_single_source_returns_same_entries()
    {
        List<IndexEntry> source =
        [
            IndexEntry.CreateBlock(TestItem.KeccakA, 1),
            IndexEntry.CreateBlock(TestItem.KeccakB, 2),
        ];

        List<IndexEntry> result = IndexTableMerger.Merge([source]);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0].CompareTo(source[0]), Is.EqualTo(0));
        Assert.That(result[1].CompareTo(source[1]), Is.EqualTo(0));
    }

    [Test]
    public void Merge_four_sources_produces_sorted_output()
    {
        // Create 4 sorted sources (simulating 4 level-0 tables)
        List<IndexEntry> s0 = [IndexEntry.CreateBlock(TestItem.KeccakA, 0)];
        List<IndexEntry> s1 = [IndexEntry.CreateBlock(TestItem.KeccakB, 1)];
        List<IndexEntry> s2 = [IndexEntry.CreateBlock(TestItem.KeccakC, 2)];
        List<IndexEntry> s3 = [IndexEntry.CreateBlock(TestItem.KeccakD, 3)];

        List<IndexEntry> result = IndexTableMerger.Merge([s0, s1, s2, s3]);

        Assert.That(result.Count, Is.EqualTo(4));

        // Verify sorted order
        for (int i = 1; i < result.Count; i++)
        {
            Assert.That(result[i - 1].CompareTo(result[i]), Is.LessThanOrEqualTo(0),
                $"Entry at index {i - 1} should be <= entry at index {i}");
        }
    }

    [Test]
    public void Merge_interleaved_entries_maintains_sort()
    {
        // Sources with different entry types that interleave when merged
        List<IndexEntry> s0 =
        [
            IndexEntry.CreateBlock(TestItem.KeccakA, 10),
            IndexEntry.CreateTransaction(TestItem.KeccakB, 10, 0, 0),
        ];
        List<IndexEntry> s1 =
        [
            IndexEntry.CreateBlock(TestItem.KeccakC, 11),
            IndexEntry.CreateLogAddress(TestItem.AddressA, 11, 0, 0),
        ];

        List<IndexEntry> result = IndexTableMerger.Merge([s0, s1]);

        Assert.That(result.Count, Is.EqualTo(4));

        // All block entries should come before tx/log entries (type 0 < type 1 < type 2)
        for (int i = 1; i < result.Count; i++)
        {
            Assert.That(result[i - 1].CompareTo(result[i]), Is.LessThanOrEqualTo(0));
        }
    }

    [Test]
    public void Merge_with_empty_source_skips_it()
    {
        List<IndexEntry> s0 = [IndexEntry.CreateBlock(TestItem.KeccakA, 0)];
        List<IndexEntry> empty = [];
        List<IndexEntry> s2 = [IndexEntry.CreateBlock(TestItem.KeccakB, 2)];

        List<IndexEntry> result = IndexTableMerger.Merge([s0, empty, s2]);

        Assert.That(result.Count, Is.EqualTo(2));
    }
}
