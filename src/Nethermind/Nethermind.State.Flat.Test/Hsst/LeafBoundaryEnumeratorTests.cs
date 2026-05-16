// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Directly drives <see cref="LeafBoundaryEnumerator"/> with synthetic
/// <c>commonPrefixArr</c> / <c>entryPositions</c> inputs to exercise the merge pass.
/// The synthetic inputs allow <c>commonPrefixArr[0]</c> to be non-zero (which is
/// impossible in real builds, where entry 0 has no predecessor), which removes the
/// "first leaf is encoded differently" wrinkle and makes adjacent splits planner-
/// compatible.
/// </summary>
[TestFixture]
public class LeafBoundaryEnumeratorTests
{
    /// <summary>Drive the enumerator to completion and collect the counts it yields.</summary>
    /// <remarks>
    /// <paramref name="pageOff"/> simulates the writer's current offset within a 4 KiB
    /// page; the enumerator uses it to force a page-fit split. Default 0 (fresh page) keeps
    /// the page-fit gate quiescent so pre-page-gate tests still cover the planner-only path.
    /// </remarks>
    private static List<int> Yields(
        byte[] commonPrefixArr, long[] entryPositions,
        int minLeafEntries, int maxLeafEntries, int keyLength,
        long pageOff = 0)
    {
        HsstBTreeBuilderBuffers buffers = new();
        try
        {
            using LeafBoundaryEnumerator iter = new(
                commonPrefixArr, entryPositions, entryPositions.Length,
                minLeafEntries, maxLeafEntries, keyLength, ref buffers);
            List<int> counts = [];
            while (iter.MoveNext(pageOff)) counts.Add(iter.Current);
            return counts;
        }
        finally
        {
            buffers.Dispose();
        }
    }

    [Test]
    public void EmptyInput_YieldsNothing()
    {
        List<int> counts = Yields([], [], minLeafEntries: 2, maxLeafEntries: 15, keyLength: 15);
        Assert.That(counts, Is.Empty);
    }

    [Test]
    public void SingleLeafFitsBudgets_YieldsOne()
    {
        byte[] cp = new byte[10];
        for (int i = 0; i < cp.Length; i++) cp[i] = 8;
        long[] pos = new long[10];

        List<int> counts = Yields(cp, pos, minLeafEntries: 2, maxLeafEntries: 20, keyLength: 15);

        Assert.That(counts, Is.EqualTo(new[] { 10 }));
    }

    /// <summary>
    /// Spike-triggered gap split produces five raw leaves; the first two have identical
    /// planner output (Uniform slot=2, prefix=8) and identical valueSlot (1, since
    /// positions are all 0), so the merger coalesces them. The three middle splits
    /// around the spike at index 9 have plans driven by the spike (slot=9, slot=5),
    /// which differ from each other and from the surrounding uniform splits, so no
    /// further merges fire.
    /// </summary>
    [Test]
    public void GapSplitWithMatchingNeighbours_CoalescesAdjacentIdenticalPlans()
    {
        byte[] cp = new byte[20];
        for (int i = 0; i < cp.Length; i++) cp[i] = 8;
        cp[9] = 13; // gap = 5 over the spike → splitter cuts
        long[] pos = new long[20];

        List<int> counts = Yields(cp, pos, minLeafEntries: 2, maxLeafEntries: 25, keyLength: 15);

        // Raw splits would be: [0..3]=4, [4..6]=3, [7..7]=1, [8..9]=2, [10..19]=10.
        // [0..3] and [4..6] both plan as Uniform slot=2 (sepLens all 9, lcp=8, effMax=1)
        // and both have valueSlot=1; they coalesce into a single 7-entry leaf.
        Assert.That(counts, Is.EqualTo(new[] { 7, 1, 2, 10 }));
    }

    /// <summary>
    /// Same shape as the merge-succeeds case, but <c>maxLeafEntries</c> is small enough
    /// that the merged count would exceed the splitter's hard cap. The merger must refuse,
    /// preserving the raw split sequence.
    /// </summary>
    [Test]
    public void CardinalityBudgetBlocksMerge()
    {
        byte[] cp = new byte[20];
        for (int i = 0; i < cp.Length; i++) cp[i] = 8;
        long[] pos = new long[20];

        // maxLeafEntries=5 forces cardinality splits and bars any merge across them.
        List<int> counts = Yields(cp, pos, minLeafEntries: 2, maxLeafEntries: 5, keyLength: 15);

        // The splitter cuts [0..19] into four 5-entry leaves with planner-compatible
        // plans (slot=2, prefix=8, valueSlot=1), but 5+5=10 > maxLeafEntries=5 so
        // every merge probe is blocked by cardinality.
        Assert.That(counts, Is.EqualTo(new[] { 5, 5, 5, 5 }));
    }

    /// <summary>
    /// Positions span a 2^24 boundary so the splitter's value-range gate triggers a cut.
    /// Each half's value range fits in a 1-byte slot, but the merged range needs 4 bytes —
    /// so the merger's value-slot equivalence check must reject the merge.
    /// </summary>
    [Test]
    public void ValueSlotWideningBlocksMerge()
    {
        byte[] cp = new byte[20];
        for (int i = 0; i < cp.Length; i++) cp[i] = 8;
        long[] pos = new long[20];
        for (int i = 0; i < 10; i++) pos[i] = i;
        for (int i = 10; i < 20; i++) pos[i] = 100_000_000L + (i - 10);

        List<int> counts = Yields(cp, pos, minLeafEntries: 2, maxLeafEntries: 25, keyLength: 15);

        // Raw splits [0..9]=10, [10..19]=10 have matching plans (slot=2, prefix=8) and
        // each individually has valueSlot=1, but the merged value range is 100M+9 →
        // valueSlot=4. The merger refuses.
        Assert.That(counts, Is.EqualTo(new[] { 10, 10 }));
    }

    /// <summary>
    /// When the bridging LCP between two splits is shorter than the buffered prefix,
    /// merging would require stripping bytes that aren't shared across the cut. The
    /// merger must refuse even if the individual plans look identical otherwise.
    /// </summary>
    [Test]
    public void BridgeLcpShorterThanBufferedPrefixBlocksMerge()
    {
        // First six entries share prefix length 8; the 7th drops the prefix to 3
        // (cp[6]=3) but the entries after it stabilize back at cp=8. The forced
        // cardinality split at maxLeafEntries=6 puts the dip exactly at the cut.
        byte[] cp = [8, 8, 8, 8, 8, 8, 3, 8, 8, 8, 8, 8];
        long[] pos = new long[cp.Length];

        List<int> counts = Yields(cp, pos, minLeafEntries: 2, maxLeafEntries: 6, keyLength: 15);

        // [0..5]=6: plan with prefix=8 (Uniform slot=2).
        // [6..11]=6: cp[6]=3 makes firstLen=4 (much smaller than the lcp the buffered
        //   plan strips), and the planner picks a different plan altogether.
        // Even if plans coincidentally matched, bridgeLcp = cp[6] = 3 < buffered prefixLen
        // would block the merge.
        Assert.That(counts, Is.EqualTo(new[] { 6, 6 }));
    }

    /// <summary>
    /// A 100-entry input with uniform LCP and zero value range fits in a single leaf
    /// when the writer is page-aligned (pageOff=0). With the writer 4000 bytes into a
    /// 4 KiB page, the page-fit gate fires repeatedly until each emitted leaf's
    /// estimated size (16 + count·2) fits in the remaining 96 bytes — so the splitter
    /// emits four 25-entry leaves and the merger refuses to coalesce them (a merged
    /// 50-entry leaf would straddle the page).
    /// </summary>
    [TestCase(0L, new[] { 100 }, TestName = "PageGate_Inactive_AtPageStart_YieldsSingleLeaf")]
    [TestCase(4000L, new[] { 25, 25, 25, 25 }, TestName = "PageGate_Active_NearPageTail_ForcesSplit")]
    public void PageFitGate_SplitsWhenLeafWouldCrossPageBoundary(long pageOff, int[] expected)
    {
        byte[] cp = new byte[100];
        for (int i = 0; i < cp.Length; i++) cp[i] = 8;
        long[] pos = new long[100];

        List<int> counts = Yields(cp, pos, minLeafEntries: 2, maxLeafEntries: 200, keyLength: 15, pageOff: pageOff);

        Assert.That(counts, Is.EqualTo(expected));
    }

    /// <summary>
    /// Even with the page-fit gate active, a leaf already at <c>minLeafEntries</c> must
    /// emit rather than recurse to zero. With minLeafEntries=2, 4 entries, and a writer
    /// offset that leaves no slack for any leaf, the splitter still produces two 2-entry
    /// leaves — the gate is policy, not a hard wall.
    /// </summary>
    [Test]
    public void PageFitGate_StopsAtMinLeafEntries()
    {
        byte[] cp = new byte[4];
        for (int i = 0; i < cp.Length; i++) cp[i] = 8;
        long[] pos = new long[4];

        // pageOff=4095 → only 1 byte of slack on the page; every leaf "crosses".
        // The gate's `count > minLeafEntries` guard prevents an infinite split:
        // raw splits drop to size 2 (=minLeafEntries) and emit.
        List<int> counts = Yields(cp, pos, minLeafEntries: 2, maxLeafEntries: 200, keyLength: 15, pageOff: 4095L);

        Assert.That(counts, Is.EqualTo(new[] { 2, 2 }));
    }

    /// <summary>
    /// Regression: the buffer reseeded after a failed merge persists across MoveNext
    /// calls. If the writer advances enough between calls that the carry-over now
    /// straddles a new 4 KiB page, the splitter must requeue the range and re-split
    /// against the new pageOff — not blindly flush the stale size. Pre-fix, the
    /// terminal leftover-flush bypassed the gate entirely and emitted the carry-over
    /// untouched, producing pageOff+leafSize &gt; 4096 crossings.
    ///
    /// Setup: 100 entries, maxLeafEntries=50 forces a cardinality split into two
    /// 50-entry raw splits. At pageOff=0 the first half emits and the second tries
    /// to merge; cardinality (50+50 &gt; 50) blocks the merge, the buf is flushed,
    /// and the second half reseeds the buf. Call 2 is invoked with pageOff=4000:
    /// the carry-over (50 entries, ~125 B estimated) no longer fits, so it gets
    /// requeued and re-split into two 25-entry leaves under the new pageOff.
    /// </summary>
    [Test]
    public void PageFitGate_RequeuesCarryOverAtAdvancedPageOff()
    {
        byte[] cp = new byte[100];
        for (int i = 0; i < cp.Length; i++) cp[i] = 8;
        long[] pos = new long[100];

        HsstBTreeBuilderBuffers buffers = new();
        try
        {
            using LeafBoundaryEnumerator iter = new(
                cp, pos, n: 100,
                minLeafEntries: 2, maxLeafEntries: 50, keyLength: 15,
                ref buffers);
            List<int> counts = [];

            // Call 1: pageOff=0. Cardinality split → emit 50, reseed with (50, 50).
            Assert.That(iter.MoveNext(0), Is.True);
            counts.Add(iter.Current);

            // Calls 2+: pageOff=4000. Carry-over re-check fires (4000 + ~125 > 4096),
            // splitter sub-splits the requeued range into 25-entry halves.
            while (iter.MoveNext(4000)) counts.Add(iter.Current);

            Assert.That(counts, Is.EqualTo(new[] { 50, 25, 25 }));
        }
        finally
        {
            buffers.Dispose();
        }
    }
}
