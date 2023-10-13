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
            cache.Set(Commitment.Zero, new byte[0]);
            cache.MemorySize.Should().Be(344);
        }

        [Test]
        public void Cache_post_capacity_growth_memory_calculated_correctly()
        {
            MemCountingCache cache = new(1024, string.Empty);
            cache.Set(TestItem._commitmentA, new byte[0]);
            cache.Set(TestItem._commitmentB, new byte[0]);
            cache.Set(TestItem._commitmentC, new byte[0]);
            cache.Set(TestItem._commitmentD, new byte[0]);
            cache.MemorySize.Should().Be(672);
        }

        [Test]
        public void Limit_by_memory_works_fine()
        {
            MemCountingCache cache = new(584, string.Empty);
            cache.Set(TestItem._commitmentA, new byte[0]);
            cache.Set(TestItem._commitmentB, new byte[0]);
            cache.Set(TestItem._commitmentC, new byte[0]);

            cache.MemorySize.Should().Be(488);
            cache.Get(TestItem._commitmentA).Should().NotBeNull();

            cache.Set(TestItem._commitmentD, new byte[0]);
            cache.MemorySize.Should().Be(488);
            cache.Get(TestItem._commitmentB).Should().BeNull();
            cache.Get(TestItem._commitmentD).Should().NotBeNull();

            cache.Set(TestItem._commitmentE, new byte[0]);
            cache.MemorySize.Should().Be(488);
            cache.Get(TestItem._commitmentB).Should().BeNull();
            cache.Get(TestItem._commitmentC).Should().BeNull();
            cache.Get(TestItem._commitmentE).Should().NotBeNull();
        }

        [Test]
        public void Limit_by_memory_works_fine_wth_deletes()
        {
            MemCountingCache cache = new(800, string.Empty);
            cache.Set(TestItem._commitmentA, new byte[0]);
            cache.Set(TestItem._commitmentB, new byte[0]);
            cache.Set(TestItem._commitmentC, new byte[0]);

            cache.Set(TestItem._commitmentA, null);

            cache.MemorySize.Should().Be(416);
            cache.Get(TestItem._commitmentA).Should().BeNull();

            cache.Set(TestItem._commitmentD, new byte[0]);
            cache.MemorySize.Should().Be(488);
            cache.Get(TestItem._commitmentB).Should().NotBeNull();
            cache.Get(TestItem._commitmentD).Should().NotBeNull();
        }
    }
}
