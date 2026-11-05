// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Follower.RollupX;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

/// <summary>The settlement cases the RollupX scanner pins, ported one for one.</summary>
public class CarrierAttributionTests
{
    private const ulong RollupId = 17;
    private static readonly ValueHash256 A = Root(0x0A);
    private static readonly ValueHash256 B = Root(0x0B);
    private static readonly ValueHash256 C = Root(0x0C);
    private static readonly ValueHash256 D = Root(0x0D);

    [Test]
    public void Attribute_EmptyWindow_IsUnsettled()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(null, [C], []), Is.EqualTo(L1Settlement.None), "nothing settled for our rollup in the window");
            Assert.That(Attribute(null, [C, B], []), Is.EqualTo(L1Settlement.None), "a shared root does not settle an empty window");
        }
    }

    [Test]
    public void Attribute_PrefixRan_EndsAtTheLastRootThatRan() =>
        Assert.That(Attribute(null, [B, C, D], [B, C]), Is.EqualTo(new L1Settlement(0, 2, C, default)), "partial consumption ends at the last root that ran");

    [Test]
    public void Attribute_WholeChainRan_EndsAtTheClaimedEnd() =>
        Assert.That(Attribute(null, [B, C], [B, C]), Is.EqualTo(new L1Settlement(0, 2, C, default)));

    [Test]
    public void Attribute_CompetitorMadeTheFirstHop_ResumesAfterIt()
    {
        Assert.That(Attribute(A, [B], [B]), Is.EqualTo(new L1Settlement(0, 1, B, A)), "the first batch makes the hop");

        L1Settlement second = Attribute(A, [B, C], [C]);

        Assert.That(second, Is.EqualTo(new L1Settlement(1, 1, C, B)), "the second batch is credited with C alone, from B");
        Assert.That(second.Effects, Is.EqualTo(new ProducingSlice(0, 1)));
    }

    [Test]
    public void Attribute_AnchorSkipped_RunStartsAtOne()
    {
        L1Settlement settlement = Attribute(null, [B, C, D], [C, D]);

        Assert.That(settlement, Is.EqualTo(new L1Settlement(1, 2, D, B)));
        Assert.That(settlement.Effects, Is.EqualTo(new ProducingSlice(0, 2)), "both producing steps ran, not one fewer");
    }

    [Test]
    public void Attribute_AnchorRan_ExcludesItFromTheEffects() =>
        Assert.That(Attribute(null, [B, C], [B, C]).Effects, Is.EqualTo(new ProducingSlice(0, 1)));

    [Test]
    public void Attribute_RunResumesMidChain_SkipsTheCompetitorsSteps()
    {
        L1Settlement settlement = Attribute(null, [B, C, D], [D]);

        Assert.That(settlement, Is.EqualTo(new L1Settlement(2, 1, D, C)), "the run begins at C, not at the claimed current root");
        Assert.That(settlement.Effects, Is.EqualTo(new ProducingSlice(1, 1)));
    }

    [Test]
    public void Attribute_DeepResume_SkipsEveryStepBeforeTheRun()
    {
        ValueHash256[] roots = [.. Enumerable.Range(0x0B, 5).Select(static b => Root((byte)b))];

        L1Settlement settlement = Attribute(null, roots, roots[3..]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((settlement.Start, settlement.Length), Is.EqualTo((3, 2)));
            Assert.That(settlement.Effects, Is.EqualTo(new ProducingSlice(2, 2)));
        }
    }

    [Test]
    public void Attribute_Matching_IsPositional()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(null, [B, B, C], [B, B]), Is.EqualTo(new L1Settlement(0, 2, B, default)), "a repeated root is located, not counted twice");
            Assert.That(Attribute(null, [B, C], [C, B]), Is.EqualTo(L1Settlement.None), "out of order matches no run");
        }
    }

    [Test]
    public void Attribute_LeadingFailedEffect_IsCarriedByTheAnchor()
    {
        L1Settlement settlement = Attribute(new ClaimedChain(null, [B, B], [true, false], [false, true]), [B]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((settlement.Start, settlement.Length), Is.EqualTo((0, 2)));
            Assert.That(settlement.Effects, Is.EqualTo(new ProducingSlice(0, 1)), "the failed entry's delivery is already committed by the anchor's root");
        }
    }

    [Test]
    public void Attribute_FailedEffects_ExpandTheirCarriers()
    {
        ClaimedChain claim = new(null, [A, A, A, B, B, C], [true, false, false, true, false, true], [false, true, true, true, true, true]);

        L1Settlement full = Attribute(claim, [A, B, C]);
        L1Settlement resumed = Attribute(claim, [B, C]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((full.Start, full.Length, full.Effects), Is.EqualTo((0, 6, new ProducingSlice(0, 5))));
            Assert.That((resumed.Start, resumed.Length, resumed.Effects, resumed.EntryState), Is.EqualTo((3, 3, new ProducingSlice(2, 3), A)));
        }
    }

    [TestCase(false, TestName = "FailedLookups")]
    [TestCase(true, TestName = "SuccessfulLookups")]
    public void Attribute_InboundLookups_KeepTheirPositionsWithoutEffects(bool lookupSucceeds)
    {
        ValueHash256 before = Root(1);
        ValueHash256 anchor = Root(2);
        ValueHash256 endpoint = Root(3);
        PostBatch batch = Batch(Update(before, anchor), Lookup(anchor, lookupSucceeds), Update(anchor, endpoint), Lookup(endpoint, lookupSucceeds));
        ClaimedChain claim = ClaimedChain.Of(batch, RollupId, new HashSet<ulong>());
        L1Settlement full = new(0, 2, endpoint, before);
        L1Settlement resumed = new(1, 1, endpoint, anchor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claim.Produces, Is.EqualTo(new[] { false, false, true, false }), "lookups produce no effect");
            Assert.That(Attribute(claim, [anchor, endpoint]), Is.EqualTo(full));
            Assert.That(Attribute(claim, [anchor]).Effects, Is.EqualTo(new ProducingSlice(0, 0)));
            Assert.That(Attribute(claim, [endpoint]), Is.EqualTo(resumed));
        }
        if (lookupSucceeds)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Attribute(claim, [anchor, anchor, endpoint]), Is.EqualTo(full), "a lookup's log may persist");
                Assert.That(Attribute(claim, [anchor, endpoint, endpoint]), Is.EqualTo(full));
                Assert.That(Attribute(claim, [anchor, anchor, endpoint, endpoint]), Is.EqualTo(full));
                Assert.That(Attribute(claim, [anchor, anchor]).Effects, Is.EqualTo(new ProducingSlice(0, 0)));
                Assert.That(Attribute(claim, [endpoint, endpoint]), Is.EqualTo(resumed));
                Assert.That(Attribute(claim, [anchor, anchor, anchor]), Is.EqualTo(L1Settlement.None), "a lookup logs at most once");
            }
        }
    }

    [Test]
    public void Attribute_InvalidEffectMask_IsUnsettled()
    {
        ValueHash256[] roots = [Root(1), Root(2), Root(3), Root(4)];
        bool[] persists = [true, true, true, true];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(new ClaimedChain(null, roots, persists, [false, true]), roots), Is.EqualTo(L1Settlement.None), "the mask covers every root");
            Assert.That(Attribute(new ClaimedChain(null, roots, persists, [true, true, true, true]), roots), Is.EqualTo(L1Settlement.None), "the anchor produces nothing");
            Assert.That(Attribute(new ClaimedChain(null, roots, persists, [false, true, true, true]), [roots[0], roots[2], roots[3]]), Is.EqualTo(L1Settlement.None),
                "a physical carrier is never passed over");
        }
    }

    [Test]
    public void Of_SkippedImmediateEntry_IsNotCarriedWithoutAMatchingRoot()
    {
        ValueHash256 current = Root(0x09);
        ClaimedChain claim = ClaimedChain.Of(Batch(Update(current, A), Update(A, B)), RollupId, new HashSet<ulong> { 1 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That((claim.CurrentRoot, claim.Roots, claim.Persists, claim.Produces), Is.EqualTo(((ValueHash256?)current, new[] { A, B }, new[] { true, false }, new[] { false, true })));
            Assert.That(Attribute(claim, [A]), Is.EqualTo(new L1Settlement(0, 1, A, current)), "the skipped effect stays unconsumed");
            Assert.That(Attribute(new ClaimedChain(current, [A, A], [true, false], [false, true]), [A]).Effects, Is.EqualTo(new ProducingSlice(0, 1)),
                "an equal-root carrier covers a pre-carried reverted outcome");
        }
    }

    [TestCase(0, TestName = "FirstSkipped")]
    [TestCase(1, TestName = "MiddleSkipped")]
    [TestCase(2, TestName = "LastSkipped")]
    public void Attribute_SkippedOutbound_KeepsPrefixAndResumeBoundaries(int committed)
    {
        ValueHash256[] roots = [Root(1), Root(2), Root(3), Root(4)];
        bool[] produces = [false, true, true, true];

        L1Settlement prefix = Attribute(new ClaimedChain(default(ValueHash256), roots, [.. Enumerable.Range(0, 4).Select(i => i <= committed)], produces), roots[..(committed + 1)]);
        L1Settlement resumed = Attribute(new ClaimedChain(default(ValueHash256), roots, [.. Enumerable.Range(0, 4).Select(i => i > committed)], produces), roots[(committed + 1)..]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((prefix.Effects, prefix.FinalState), Is.EqualTo((new ProducingSlice(0, committed), roots[committed])));
            Assert.That((resumed.Effects, resumed.EntryState, resumed.FinalState), Is.EqualTo((new ProducingSlice(committed, 3 - committed), roots[committed], roots[3])));
        }
    }

    [Test]
    public void Attribute_UnclaimedRoot_IsUnsettled() =>
        Assert.That(Attribute(null, [B], [Root(0x7A)]), Is.EqualTo(L1Settlement.None));

    private static L1Settlement Attribute(ValueHash256? current, ValueHash256[] roots, ValueHash256[] observed) =>
        Attribute(new ClaimedChain(current, roots, [.. roots.Select(static _ => true)], [.. roots.Select(static (_, i) => i != 0)]), observed);

    private static L1Settlement Attribute(ClaimedChain claim, ValueHash256[] observed) => CarrierAttribution.Attribute(claim, observed);

    private static ValueHash256 Root(byte value)
    {
        byte[] bytes = new byte[32];
        bytes[^1] = value;
        bytes[0] = value;
        return new ValueHash256(bytes);
    }

    private static PostBatch Batch(params ExecutionEntry[] entries) => new([], entries, [], 1, 0, [], [], [], [], [], 0, false);

    private static ExecutionEntry Update(in ValueHash256 current, in ValueHash256 next) =>
        new([new RollupUpdate(RollupId, current, next, 0)], default, [], [], default, 0, true, []);

    private static ExecutionEntry Lookup(in ValueHash256 root, bool success)
    {
        ValueHash256 proxy = Root(0xAB);
        RollupUpdate update = new(RollupId, root, root, 0);
        return new ExecutionEntry([update], proxy, [], [], RollingHash.SeedL1(update, proxy), RollupId, success, []);
    }
}
