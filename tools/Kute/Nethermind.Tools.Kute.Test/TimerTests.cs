// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test;

public class TimerTests
{
    [Test]
    public async Task Timer_ComputesElapsedTime()
    {
        var t = new Timer();
        using (t.Time())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        t.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(90));
        t.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(110));
    }

    [Test]
    public async Task Timer_AddsAllElapsedTimes()
    {
        var t = new Timer();
        using (t.Time())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        using (t.Time())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        t.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(90));
        t.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(110));
    }

    [Test]
    public async Task Timer_IgnoresTimeOutsideOfUsing()
    {
        var t = new Timer();
        using (t.Time())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50));

        t.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(40));
        t.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(60));
    }
}
