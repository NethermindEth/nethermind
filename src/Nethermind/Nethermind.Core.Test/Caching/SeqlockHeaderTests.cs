// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching;

[TestFixture]
public class SeqlockHeaderTests
{
    [Test]
    public void Sequential_eviction_seeds_sample_every_way_about_equally()
    {
        // Evictions seed the sample with per-set stamps that step by one; a skewed sample keeps the stale entries of
        // the ways it skips and evicts newer ones in their place.
        for (long start = 0; start < 20_000; start += 64)
        {
            int[] sampled = new int[8];
            for (int k = 0; k < 32; k++)
            {
                (int a, int b, int c) = SeqlockHeader.Pick3Indices(start + 2 * k);
                sampled[a]++;
                sampled[b]++;
                sampled[c]++;
            }

            // Each way is in about 12 of 32 samples of 3 distinct ways from 8.
            Assert.That(sampled, Is.All.InRange(2, 24), $"32 evictions seeded from {start}");
        }
    }
}
