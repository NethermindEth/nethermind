// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V72;

public class CellServeTokenBucketTests
{
    [Test]
    public void Refills_at_the_configured_rate_and_caps_refills_and_refunds()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        CellServeTokenBucket bucket = new(100, 10, now);
        Assert.That(bucket.TryConsume(100, now), Is.True);
        Assert.That(bucket.TryConsume(1, now), Is.False);

        now = now.AddSeconds(1);
        Assert.That(bucket.TryConsume(10, now), Is.True);
        Assert.That(bucket.TryConsume(1, now), Is.False);

        bucket.Refund(1000);
        Assert.That(bucket.TryConsume(100, now), Is.True);
        Assert.That(bucket.TryConsume(1, now), Is.False);

        now = now.AddSeconds(1000);
        Assert.That(bucket.TryConsume(100, now), Is.True);
        Assert.That(bucket.TryConsume(1, now), Is.False);
    }
}
