// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class HashCacheTests
{
    [Test]
    public void Clearing_a_nonce_gap_forgets_all_its_tracked_hashes()
    {
        HashCache cache = new(0);
        Hash256 first = TestItem.KeccakA;
        Hash256 second = TestItem.KeccakB;
        Hash256 otherGap = TestItem.KeccakC;

        cache.SetForCurrentBlock(first);
        cache.SetForCurrentBlock(second);
        cache.SetForCurrentBlock(otherGap);
        cache.SetNonceGap(TestItem.AddressA, 0, first);
        cache.SetNonceGap(TestItem.AddressA, 0, second);
        cache.SetNonceGap(TestItem.AddressA, 1, otherGap);

        cache.DeleteNonceGap(TestItem.AddressA, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Get(first), Is.False);
            Assert.That(cache.Get(second), Is.False);
            Assert.That(cache.Get(otherGap), Is.True);
        }
    }

    [Test]
    public void A_nonce_gap_tracks_at_most_four_distinct_hashes()
    {
        HashCache cache = new(0);
        Hash256[] hashes = [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, TestItem.KeccakD, TestItem.KeccakE];
        foreach (Hash256 hash in hashes)
        {
            cache.SetForCurrentBlock(hash);
            cache.SetNonceGap(TestItem.AddressA, 0, hash);
            cache.SetNonceGap(TestItem.AddressA, 0, hash);
        }

        cache.DeleteNonceGap(TestItem.AddressA, 0);

        Assert.That(hashes.Where(hash => cache.Get(hash)), Is.EqualTo(new[] { TestItem.KeccakE }));
    }
}
