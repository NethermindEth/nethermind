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
        AssertAvailable(100);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bucket.TryConsume(1, now.AddSeconds(-10)), Is.False);
            Assert.That(bucket.TryConsume(1, now), Is.False);
        }

        now = now.AddSeconds(1);
        AssertAvailable(10);

        bucket.Refund(1000);
        AssertAvailable(100);

        now = now.AddSeconds(1000);
        AssertAvailable(100);

        void AssertAvailable(int expected)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bucket.TryConsume(expected, now), Is.True);
                Assert.That(bucket.TryConsume(1, now), Is.False);
            }
        }
    }
}
