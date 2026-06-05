// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Pins the per-slice read-coverage lanes: the structural bitmap reports the first uncovered declared
/// read (the implied all-bits-set expectation), the chargeable count is distinct within a slice, and
/// <see cref="BalReadCoverage.Absorb"/> OR-reduces slice bitmaps while summing chargeable counts.
/// </summary>
[TestFixture]
public class BalReadCoverageTests
{
    [Test]
    public void All_marked_reports_fully_covered()
    {
        using BalReadCoverage coverage = new(70); // spans two words + partial last word
        for (int i = 0; i < 70; i++) coverage.MarkRead(i, chargeable: false);

        Assert.That(coverage.TryFindFirstUncovered(out _), Is.False);
    }

    [TestCase(0)]
    [TestCase(5)]
    [TestCase(63)]
    [TestCase(64)]
    [TestCase(69)]
    public void Single_unmarked_ordinal_is_found(int missing)
    {
        using BalReadCoverage coverage = new(70);
        for (int i = 0; i < 70; i++)
        {
            if (i != missing) coverage.MarkRead(i, chargeable: false);
        }

        Assert.That(coverage.TryFindFirstUncovered(out int ordinal), Is.True);
        Assert.That(ordinal, Is.EqualTo(missing));
    }

    [Test]
    public void Empty_coverage_is_vacuously_covered()
    {
        using BalReadCoverage coverage = new(0);

        Assert.That(coverage.Count, Is.EqualTo(0));
        Assert.That(coverage.TryFindFirstUncovered(out _), Is.False);
        Assert.That(coverage.ChargeableCount, Is.EqualTo(0));
    }

    [Test]
    public void First_uncovered_is_lowest_ordinal()
    {
        using BalReadCoverage coverage = new(70);
        coverage.MarkRead(0, false); // leave 1..69 uncovered

        Assert.That(coverage.TryFindFirstUncovered(out int ordinal), Is.True);
        Assert.That(ordinal, Is.EqualTo(1));
    }

    [Test]
    public void Repeat_in_same_slice_counts_chargeable_once()
    {
        using BalReadCoverage coverage = new(4);
        coverage.MarkRead(2, chargeable: true);
        coverage.MarkRead(2, chargeable: true);

        Assert.That(coverage.ChargeableCount, Is.EqualTo(1));
    }

    [Test]
    public void Same_ordinal_across_slices_counts_chargeable_per_slice()
    {
        // Distinct slices are distinct coverages OR-reduced at block end: each counts the ordinal once.
        using BalReadCoverage sliceA = new(4);
        using BalReadCoverage sliceB = new(4);
        sliceA.MarkRead(2, chargeable: true);
        sliceB.MarkRead(2, chargeable: true);

        sliceA.Absorb(sliceB);
        Assert.That(sliceA.ChargeableCount, Is.EqualTo(2));
    }

    [Test]
    public void Non_chargeable_read_covers_but_does_not_count()
    {
        using BalReadCoverage coverage = new(4);
        coverage.MarkRead(1, chargeable: false);

        Assert.That(coverage.ChargeableCount, Is.EqualTo(0));
        coverage.MarkRead(0, true);
        coverage.MarkRead(2, true);
        coverage.MarkRead(3, true);
        Assert.That(coverage.TryFindFirstUncovered(out _), Is.False); // structural lane still marked
    }

    [Test]
    public void Absorb_ors_coverage_and_sums_chargeable()
    {
        using BalReadCoverage a = new(70);
        using BalReadCoverage b = new(70);

        // Disjoint coverage across two slices; together they cover everything.
        for (int i = 0; i < 70; i++)
        {
            if ((i & 1) == 0) a.MarkRead(i, chargeable: true);
            else b.MarkRead(i, chargeable: true);
        }

        Assert.That(a.TryFindFirstUncovered(out int beforeOrdinal), Is.True);
        Assert.That(beforeOrdinal, Is.EqualTo(1)); // a alone misses the odd ordinals

        a.Absorb(b);

        Assert.That(a.TryFindFirstUncovered(out _), Is.False);
        Assert.That(a.ChargeableCount, Is.EqualTo(70)); // 35 from each slice
    }
}
