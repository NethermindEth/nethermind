// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.History.Walk;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class WalkProgressTests
{
    [Test]
    public void Eta_LongMainnetWalk_DoesNotOverflow()
    {
        // 512 items x 10,000 units at 27% done after 69 h: elapsed ticks * remaining exceeds long.MaxValue (#13760).
        const long total = 512L * 10_000;
        const long done = total * 27 / 100;
        TimeSpan elapsed = TimeSpan.FromHours(69);
        Assert.That(() => elapsed * (total - done), Throws.TypeOf<OverflowException>(), "the old multiply-first order overflows here");

        Assert.That(WalkProgress.Eta(elapsed, total - done, done), Is.EqualTo("7d 18h"));
    }

    [Test]
    public void Eta_BeyondTimeSpanRange_IsUnknown() =>
        Assert.That(WalkProgress.Eta(TimeSpan.FromDays(365), long.MaxValue, 1), Is.EqualTo("n/a"));

    [TestCase(0)]
    [TestCase(-1)]
    public void Eta_WithoutProgressThisRun_IsUnknown(double doneThisRun) =>
        Assert.That(WalkProgress.Eta(TimeSpan.FromHours(1), 100, doneThisRun), Is.EqualTo("n/a"));
}
