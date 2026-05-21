// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test;

public class TimerTests
{
    [Test]
    public async Task Timer_ComputesElapsedTime()
    {
        Timer t = new();
        using (t.Time())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.That(t.Elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(90)));
        Assert.That(t.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(110)));
    }

    [Test]
    public async Task Timer_AddsAllElapsedTimes()
    {
        Timer t = new();
        using (t.Time())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        using (t.Time())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.That(t.Elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(90)));
        Assert.That(t.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(110)));
    }

    [Test]
    public async Task Timer_IgnoresTimeOutsideOfUsing()
    {
        Timer t = new();
        using (t.Time())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.That(t.Elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(40)));
        Assert.That(t.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(60)));
    }
}
