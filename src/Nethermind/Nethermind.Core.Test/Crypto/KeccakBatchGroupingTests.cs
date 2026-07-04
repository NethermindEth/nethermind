// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

[TestFixture]
public class KeccakBatchGroupingTests
{
    [TestCase(0, 1)]     // empty message: one padding block
    [TestCase(1, 1)]
    [TestCase(135, 1)]   // last length that still fits one block
    [TestCase(136, 2)]   // full rate forces a second (padding) block
    [TestCase(137, 2)]
    [TestCase(271, 2)]
    [TestCase(272, 3)]
    public void BlockCount_MatchesCeilFormula(int length, int expected) =>
        Assert.That(KeccakBatchGrouping.BlockCount(length), Is.EqualTo(expected));

    [TestCase(-1)]
    [TestCase(-136)]
    public void BlockCount_NegativeLength_Throws(int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => KeccakBatchGrouping.BlockCount(length));

    [Test]
    public void EmptyBatch_ProducesNoGroups()
    {
        int[] permutation = [];
        int[] boundaries = [];
        int groups = KeccakBatchGrouping.GroupByBlockCount(ReadOnlySpan<int>.Empty, permutation, boundaries);
        Assert.That(groups, Is.EqualTo(0));
    }

    [Test]
    public void SingleMessage_OneGroupCoveringIt()
    {
        int[] offsets = [200]; // one 200-byte message -> 2 blocks
        AssertGrouping(offsets, expectedPermutation: [0], expectedBoundaries: [1]);
    }

    [Test]
    public void NineEqualLengthMessages_FormOneGroup()
    {
        // Nine 100-byte messages: identical block count (1) -> a single group spanning all nine, identity permutation.
        int[] offsets = Offsets(Repeat(length: 100, times: 9));
        AssertGrouping(offsets, expectedPermutation: [0, 1, 2, 3, 4, 5, 6, 7, 8], expectedBoundaries: [9]);
    }

    [Test]
    public void MessagesSpanningFourBlockCounts_FourGroupsSortedAscending()
    {
        // Block counts 3,1,4,2 presented out of order, one message each, to prove the ascending sort and per-count grouping.
        int[] lengths = [300 /*3*/, 10 /*1*/, 500 /*4*/, 200 /*2*/];
        int[] offsets = Offsets(lengths);
        // Sorted ascending by block count: index1(1), index3(2), index0(3), index2(4); one message each -> 4 groups.
        AssertGrouping(offsets, expectedPermutation: [1, 3, 0, 2], expectedBoundaries: [1, 2, 3, 4]);
    }

    [Test]
    public void SevenMixed_StableWithinEqualBlockCounts()
    {
        // Block counts: 1,2,1,2,3,1,2 -> groups {1:idx0,2,5}, {2:idx1,3,6}, {3:idx4}; stable order preserved within each.
        int[] lengths = [10 /*1*/, 200 /*2*/, 50 /*1*/, 150 /*2*/, 300 /*3*/, 100 /*1*/, 250 /*2*/];
        int[] offsets = Offsets(lengths);
        AssertGrouping(
            offsets,
            expectedPermutation: [0, 2, 5, 1, 3, 6, 4],
            expectedBoundaries: [3, 6, 7]);
    }

    [Test]
    public void ComputeBlockCounts_MatchesPerMessageFormula()
    {
        int[] lengths = [0, 135, 136, 600];
        int[] offsets = Offsets(lengths);
        int[] blockCounts = new int[lengths.Length];

        KeccakBatchGrouping.ComputeBlockCounts(offsets, blockCounts);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < lengths.Length; i++)
            {
                Assert.That(blockCounts[i], Is.EqualTo(KeccakBatchGrouping.BlockCount(lengths[i])), $"index {i}");
            }
        }
    }

    [Test]
    public void GroupByBlockCount_ThrowsWhenBuffersTooShort()
    {
        int[] offsets = [10, 20];
        Assert.Throws<ArgumentException>(() => KeccakBatchGrouping.GroupByBlockCount(offsets, new int[1], new int[2]));
        Assert.Throws<ArgumentException>(() => KeccakBatchGrouping.GroupByBlockCount(offsets, new int[2], new int[1]));
    }

    [Test]
    public void ComputeBlockCounts_ThrowsWhenOutputTooShort()
    {
        int[] offsets = [10, 20];
        Assert.Throws<ArgumentException>(() => KeccakBatchGrouping.ComputeBlockCounts(offsets, new int[1]));
    }

    [Test]
    public void NonMonotonicOffsets_Throw()
    {
        int[] offsets = [20, 10]; // second offset descends below the first
        Assert.Throws<ArgumentException>(() => KeccakBatchGrouping.ComputeBlockCounts(offsets, new int[2]));
        Assert.Throws<ArgumentException>(() => KeccakBatchGrouping.GroupByBlockCount(offsets, new int[2], new int[2]));
    }

    // Verifies the permutation and boundaries, and independently re-checks the invariants a consumer relies on:
    // permutation is a bijection of [0..n), block counts are non-decreasing along it, and each boundary ends a run.
    private static void AssertGrouping(int[] offsets, int[] expectedPermutation, int[] expectedBoundaries)
    {
        int n = offsets.Length;
        int[] permutation = new int[n];
        int[] boundaries = new int[KeccakBatchGrouping.MaxGroups(n)];

        int groups = KeccakBatchGrouping.GroupByBlockCount(offsets, permutation, boundaries);

        int[] blockCounts = new int[n];
        KeccakBatchGrouping.ComputeBlockCounts(offsets, blockCounts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(permutation, Is.EqualTo(expectedPermutation), "permutation");
            Assert.That(groups, Is.EqualTo(expectedBoundaries.Length), "group count");
            Assert.That(boundaries[..groups], Is.EqualTo(expectedBoundaries), "boundaries");

            bool[] seen = new bool[n];
            for (int i = 0; i < n; i++)
            {
                Assert.That(seen[permutation[i]], Is.False, $"permutation repeats index {permutation[i]}");
                seen[permutation[i]] = true;
                if (i > 0)
                {
                    Assert.That(blockCounts[permutation[i]], Is.GreaterThanOrEqualTo(blockCounts[permutation[i - 1]]), "block counts non-decreasing");
                }
            }

            int prev = 0;
            for (int g = 0; g < groups; g++)
            {
                Assert.That(boundaries[g], Is.GreaterThan(prev), $"group {g} is non-empty");
                int groupBlockCount = blockCounts[permutation[prev]];
                for (int k = prev; k < boundaries[g]; k++)
                {
                    Assert.That(blockCounts[permutation[k]], Is.EqualTo(groupBlockCount), $"group {g} is uniform");
                }
                prev = boundaries[g];
            }
            Assert.That(prev, Is.EqualTo(n), "groups cover the whole batch");
        }
    }

    private static int[] Repeat(int length, int times)
    {
        int[] lengths = new int[times];
        Array.Fill(lengths, length);
        return lengths;
    }

    private static int[] Offsets(IReadOnlyList<int> lengths)
    {
        int[] offsets = new int[lengths.Count];
        int total = 0;
        for (int i = 0; i < lengths.Count; i++)
        {
            total += lengths[i];
            offsets[i] = total;
        }
        return offsets;
    }
}
