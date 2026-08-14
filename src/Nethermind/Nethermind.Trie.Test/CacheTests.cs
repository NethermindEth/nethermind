// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class CacheTests
    {
        [Test]
        public void Cache_initial_memory_calculated_correctly()
        {
            MemCountingCache cache = new(1024, string.Empty);
            Assert.That(cache.MemorySize, Is.EqualTo(136));
        }

        [Test]
        public void Cache_post_init_memory_calculated_correctly()
        {
            MemCountingCache cache = new(1024, string.Empty);
            cache.Set(Keccak.Zero, []);
            Assert.That(cache.MemorySize, Is.EqualTo(344));
        }

        [Test]
        public void Cache_post_capacity_growth_memory_calculated_correctly()
        {
            MemCountingCache cache = new(1024, string.Empty);
            cache.Set(TestItem.KeccakA, []);
            cache.Set(TestItem.KeccakB, []);
            cache.Set(TestItem.KeccakC, []);
            cache.Set(TestItem.KeccakD, []);
            Assert.That(cache.MemorySize, Is.EqualTo(672));
        }

        [Test]
        public void Limit_by_memory_works_fine()
        {
            MemCountingCache cache = new(584, string.Empty);
            cache.Set(TestItem.KeccakA, []);
            cache.Set(TestItem.KeccakB, []);
            cache.Set(TestItem.KeccakC, []);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.MemorySize, Is.EqualTo(488));
                Assert.That(cache.Get(TestItem.KeccakA), Is.Not.Null);
            }

            cache.Set(TestItem.KeccakD, []);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.MemorySize, Is.EqualTo(488));
                Assert.That(cache.Get(TestItem.KeccakB), Is.Null);
                Assert.That(cache.Get(TestItem.KeccakD), Is.Not.Null);
            }

            cache.Set(TestItem.KeccakE, []);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.MemorySize, Is.EqualTo(488));
                Assert.That(cache.Get(TestItem.KeccakB), Is.Null);
                Assert.That(cache.Get(TestItem.KeccakC), Is.Null);
                Assert.That(cache.Get(TestItem.KeccakE), Is.Not.Null);
            }
        }

        [Test]
        public void Limit_by_memory_works_fine_wth_deletes()
        {
            MemCountingCache cache = new(800, string.Empty);
            cache.Set(TestItem.KeccakA, []);
            cache.Set(TestItem.KeccakB, []);
            cache.Set(TestItem.KeccakC, []);

            cache.Set(TestItem.KeccakA, null);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.MemorySize, Is.EqualTo(416));
                Assert.That(cache.Get(TestItem.KeccakA), Is.Null);
            }

            cache.Set(TestItem.KeccakD, []);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.MemorySize, Is.EqualTo(488));
                Assert.That(cache.Get(TestItem.KeccakB), Is.Not.Null);
                Assert.That(cache.Get(TestItem.KeccakD), Is.Not.Null);
            }
        }
    }
}
