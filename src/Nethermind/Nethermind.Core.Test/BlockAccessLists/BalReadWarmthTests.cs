// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Pins the EIP-2929 warm/cold semantics of <see cref="BalReadWarmth"/>: the <see cref="BalReadWarmth.WarmUp"/>
/// return value is the cold/warm decision that selects ColdSLoad vs WarmStateRead gas, so the cold-&gt;warm
/// transition, the repeat-access dedup, and the journaled un-warm on sub-frame revert must all be exact.
/// </summary>
[TestFixture]
public class BalReadWarmthTests
{
    [Test]
    public void First_access_is_cold_second_is_warm()
    {
        using BalReadWarmth warmth = new(4);

        Assert.That(warmth.WarmUp(2), Is.True, "first access must be cold (charged ColdSLoad)");
        Assert.That(warmth.WarmUp(2), Is.False, "repeat access must be warm (charged WarmStateRead)");
        Assert.That(warmth.IsWarm(2), Is.True);
    }

    [Test]
    public void Distinct_ordinals_are_independently_cold()
    {
        using BalReadWarmth warmth = new(4);

        Assert.That(warmth.WarmUp(0), Is.True);
        Assert.That(warmth.WarmUp(1), Is.True);
        Assert.That(warmth.IsWarm(0), Is.True);
        Assert.That(warmth.IsWarm(2), Is.False);
    }

    [Test]
    public void Restore_unwarms_ordinals_warmed_after_the_snapshot()
    {
        using BalReadWarmth warmth = new(4);
        warmth.WarmUp(0);
        int snapshot = warmth.TakeSnapshot();
        warmth.WarmUp(1);
        warmth.WarmUp(2);

        warmth.Restore(snapshot);

        Assert.That(warmth.IsWarm(0), Is.True, "warmed before the snapshot - survives revert");
        Assert.That(warmth.IsWarm(1), Is.False, "warmed after the snapshot - rolled back");
        Assert.That(warmth.IsWarm(2), Is.False);
    }

    [Test]
    public void Read_warmed_then_reverted_is_cold_again()
    {
        // Drives gas correctness: a sub-call warms a slot, reverts, the outer frame re-reads it -> cold again.
        using BalReadWarmth warmth = new(4);
        int snapshot = warmth.TakeSnapshot();
        Assert.That(warmth.WarmUp(3), Is.True);

        warmth.Restore(snapshot);

        Assert.That(warmth.WarmUp(3), Is.True, "after revert the read is cold and charged ColdSLoad again");
    }

    [Test]
    public void Nested_snapshots_restore_each_layer_independently()
    {
        using BalReadWarmth warmth = new(8);
        warmth.WarmUp(0);
        int outer = warmth.TakeSnapshot();
        warmth.WarmUp(1);
        int inner = warmth.TakeSnapshot();
        warmth.WarmUp(2);

        warmth.Restore(inner);
        Assert.That(warmth.IsWarm(0), Is.True);
        Assert.That(warmth.IsWarm(1), Is.True);
        Assert.That(warmth.IsWarm(2), Is.False);

        warmth.Restore(outer);
        Assert.That(warmth.IsWarm(0), Is.True);
        Assert.That(warmth.IsWarm(1), Is.False);
    }

    [Test]
    public void Restore_to_zero_unwarms_everything()
    {
        using BalReadWarmth warmth = new(70); // spans two words + partial
        for (int i = 0; i < 70; i++) warmth.WarmUp(i);

        warmth.Restore(0);

        for (int i = 0; i < 70; i++) Assert.That(warmth.IsWarm(i), Is.False, $"ordinal {i} should be cold after full revert");
    }

    [Test]
    public void Reset_clears_all_warmth_and_journal()
    {
        using BalReadWarmth warmth = new(4);
        warmth.WarmUp(1);
        warmth.WarmUp(3);

        warmth.Reset();

        Assert.That(warmth.IsWarm(1), Is.False);
        Assert.That(warmth.IsWarm(3), Is.False);
        Assert.That(warmth.TakeSnapshot(), Is.EqualTo(0), "journal is empty after reset");
        Assert.That(warmth.WarmUp(1), Is.True, "post-reset access is cold again");
    }

    // 1000 ordinals = 15 full words + a partial word: exercises bit addressing across word boundaries
    // and the journaled un-warm of a partial, scattered set.
    [TestCase(0)]
    [TestCase(63)]
    [TestCase(64)]
    [TestCase(500)]
    [TestCase(999)]
    public void Large_space_warms_and_reverts_at_word_boundaries(int ordinal)
    {
        using BalReadWarmth warmth = new(1000);
        int snapshot = warmth.TakeSnapshot();

        Assert.That(warmth.WarmUp(ordinal), Is.True);
        Assert.That(warmth.IsWarm(ordinal), Is.True);

        warmth.Restore(snapshot);
        Assert.That(warmth.IsWarm(ordinal), Is.False);
    }

    [Test]
    public void Reverted_then_rewarmed_ordinal_is_journaled_again()
    {
        // The journal must re-track a re-warmed ordinal so a second revert un-warms it too.
        using BalReadWarmth warmth = new(4);
        int snapshot = warmth.TakeSnapshot();
        warmth.WarmUp(1);
        warmth.Restore(snapshot);
        warmth.WarmUp(1); // cold->warm again, re-journaled

        warmth.Restore(snapshot);

        Assert.That(warmth.IsWarm(1), Is.False);
    }

    [Test]
    public void Empty_space_supports_reset_and_dispose()
    {
        using BalReadWarmth warmth = new(0);
        warmth.Reset();
        Assert.That(warmth.TakeSnapshot(), Is.EqualTo(0));
    }
}
