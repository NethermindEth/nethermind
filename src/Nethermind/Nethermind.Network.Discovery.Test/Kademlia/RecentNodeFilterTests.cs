// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Kademlia;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class RecentNodeFilterTests
{
    [TestCase(0, 64)]
    [TestCase(4, 1024)]
    [TestCase(16, 4096)]
    [TestCase(160, 4096)]
    public void GetLimit_should_cap_large_bucket_multiplier(int bucketSize, int expected)
        => Assert.That(RecentNodeFilter.GetLimit(bucketSize, maxDistance: 256, minimumCount: 64), Is.EqualTo(expected));

    [Test]
    public void TryReserve_should_reject_recent_node_until_released()
    {
        RecentNodeFilter<string> filter = new(2);

        Assert.That(filter.TryReserve("a"), Is.True);
        Assert.That(filter.TryReserve("a"), Is.False);

        filter.Release("a");

        Assert.That(filter.TryReserve("a"), Is.True);
    }

    [Test]
    public void TryReserve_should_evict_oldest_active_node_when_limit_is_exceeded()
    {
        RecentNodeFilter<string> filter = new(2);

        Assert.That(filter.TryReserve("a"), Is.True);
        Assert.That(filter.TryReserve("b"), Is.True);
        Assert.That(filter.TryReserve("c"), Is.True);

        Assert.That(filter.TryReserve("a"), Is.True);
    }
}
