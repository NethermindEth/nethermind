// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Threading;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class AddressBurstLimiterTests
{
    [Test]
    public void Exhausted_and_full_filters_recover_at_window_boundary()
    {
        ManualTimeProvider time = new();
        AddressBurstLimiter limiter = new(1, 1, TimeSpan.FromMilliseconds(100), time);
        IPAddress first = IPAddress.Parse("8.8.8.8");
        IPAddress other = IPAddress.Parse("8.8.8.9");
        Assert.That(limiter.TryAccept(first), Is.True);
        time.Advance(TimeSpan.FromMilliseconds(99));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(limiter.TryAccept(first), Is.False);
            Assert.That(limiter.TryAccept(other), Is.False);
        }
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.That(limiter.TryAccept(other), Is.True);
        Assert.That(limiter.TryAccept(other), Is.False);
    }

    [Test]
    public void Concurrent_packets_cannot_exceed_source_budget()
    {
        AddressBurstLimiter limiter = new(16, 32, TimeSpan.FromDays(1));
        int accepted = 0;
        Parallel.For(0, 128, i =>
        {
            if (limiter.TryAccept(IPAddress.Parse($"2001:4860:1:2::{i:x}"))) Interlocked.Increment(ref accepted);
        });
        Assert.That(accepted, Is.EqualTo(16));
    }

    [TestCase("2001:4860:1:2::1", "2001:4860:1:2::ffff", false)]
    [TestCase("2001:4860:1:2::1", "2001:4860:1:3::1", true)]
    [TestCase("8.8.8.8", "::ffff:8.8.8.8", false)]
    [TestCase("8.8.8.8", "8.8.8.9", true)]
    public void Shares_budget_by_ipv6_subnet_and_exact_ipv4(string first, string second, bool accepted)
    {
        AddressBurstLimiter limiter = new(2, 8, TimeSpan.FromDays(1));
        Assert.That(limiter.TryAccept(IPAddress.Parse(first)), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(limiter.TryAccept(IPAddress.Parse(first)), Is.True);
            Assert.That(limiter.TryAccept(IPAddress.Parse(second)), Is.EqualTo(accepted));
        }
    }

    [Test]
    public void Full_filter_does_not_reset_exhausted_source_budget()
    {
        AddressBurstLimiter limiter = new(1, 2, TimeSpan.FromDays(1));
        IPAddress first = IPAddress.Parse("8.8.8.8");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(limiter.TryAccept(first), Is.True);
            Assert.That(limiter.TryAccept(IPAddress.Parse("8.8.8.9")), Is.True);
            Assert.That(limiter.TryAccept(IPAddress.Parse("8.8.8.10")), Is.False);
            Assert.That(limiter.TryAccept(first), Is.False);
        }
    }
}
