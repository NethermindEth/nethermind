// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    public class CacheTests
    {
        [Test]
        public void Cache_initial_memory_calculated_correctly()
        {
            MemCountingCache cache = new(1024, string.Empty);
            cache.MemorySize.Should().Be(136);
        }

        [Test]
        public void Cache_post_init_memory_calculated_correctly()
        {
            MemCountingCache cache = new(1024, string.Empty);
            cache.Set(Keccak.Zero, new byte[0]);
            cache.MemorySize.Should().Be(376);
        }

        [Test]
        public void Cache_post_capacity_growth_memory_calculated_correctly()
        {
            MemCountingCache cache = new(1024, string.Empty);
            cache.Set(TestItem.KeccakA, new byte[0]);
            cache.Set(TestItem.KeccakB, new byte[0]);
            cache.Set(TestItem.KeccakC, new byte[0]);
            cache.Set(TestItem.KeccakD, new byte[0]);
            cache.MemorySize.Should().Be(800);
        }

        [Test]
        public void Limit_by_memory_works_fine()
        {
            MemCountingCache cache = new(584, string.Empty);
            cache.Set(TestItem.KeccakA, new byte[0]);
            cache.Set(TestItem.KeccakB, new byte[0]);
            cache.Set(TestItem.KeccakC, new byte[0]);

            cache.MemorySize.Should().Be(584);
            cache.Get(TestItem.KeccakA).Should().NotBeNull();

            cache.Set(TestItem.KeccakD, new byte[0]);
            cache.MemorySize.Should().Be(584);
            cache.Get(TestItem.KeccakB).Should().BeNull();
            cache.Get(TestItem.KeccakD).Should().NotBeNull();

            cache.Set(TestItem.KeccakE, new byte[0]);
            cache.MemorySize.Should().Be(584);
            cache.Get(TestItem.KeccakB).Should().BeNull();
            cache.Get(TestItem.KeccakC).Should().BeNull();
            cache.Get(TestItem.KeccakE).Should().NotBeNull();
        }

        [Test]
        public void Limit_by_memory_works_fine_wth_deletes()
        {
            MemCountingCache cache = new(800, string.Empty);
            cache.Set(TestItem.KeccakA, new byte[0]);
            cache.Set(TestItem.KeccakB, new byte[0]);
            cache.Set(TestItem.KeccakC, new byte[0]);

            cache.Set(TestItem.KeccakA, null);

            cache.MemorySize.Should().Be(480);
            cache.Get(TestItem.KeccakA).Should().BeNull();

            cache.Set(TestItem.KeccakD, new byte[0]);
            cache.MemorySize.Should().Be(584);
            cache.Get(TestItem.KeccakB).Should().NotBeNull();
            cache.Get(TestItem.KeccakD).Should().NotBeNull();
        }
    }
}
