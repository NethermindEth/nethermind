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
            cache.Set(in Keccak.Zero.ValueKeccak, new byte[0]);
            cache.MemorySize.Should().Be(344);
        }

        [Test]
        public void Cache_post_capacity_growth_memory_calculated_correctly()
        {
            MemCountingCache cache = new(1024, string.Empty);
            cache.Set(in TestItem.KeccakA.ValueKeccak, new byte[0]);
            cache.Set(in TestItem.KeccakB.ValueKeccak, new byte[0]);
            cache.Set(in TestItem.KeccakC.ValueKeccak, new byte[0]);
            cache.Set(in TestItem.KeccakD.ValueKeccak, new byte[0]);
            cache.MemorySize.Should().Be(672);
        }

        [Test]
        public void Limit_by_memory_works_fine()
        {
            MemCountingCache cache = new(500, string.Empty);
            cache.Set(in TestItem.KeccakA.ValueKeccak, new byte[0]);
            cache.Set(in TestItem.KeccakB.ValueKeccak, new byte[0]);
            cache.Set(in TestItem.KeccakC.ValueKeccak, new byte[0]);

            cache.MemorySize.Should().Be(488);
            cache.Get(TestItem.KeccakA.ValueKeccak).Should().NotBeNull();

            cache.Set(in TestItem.KeccakD.ValueKeccak, new byte[0]);
            cache.MemorySize.Should().Be(488);
            cache.Get(in TestItem.KeccakB.ValueKeccak).Should().BeNull();
            cache.Get(in TestItem.KeccakD.ValueKeccak).Should().NotBeNull();

            cache.Set(in TestItem.KeccakE.ValueKeccak, new byte[0]);
            cache.MemorySize.Should().Be(488);
            cache.Get(in TestItem.KeccakB.ValueKeccak).Should().BeNull();
            cache.Get(in TestItem.KeccakC.ValueKeccak).Should().BeNull();
            cache.Get(in TestItem.KeccakE.ValueKeccak).Should().NotBeNull();
        }

        [Test]
        public void Limit_by_memory_works_fine_wth_deletes()
        {
            MemCountingCache cache = new(800, string.Empty);
            cache.Set(in TestItem.KeccakA.ValueKeccak, new byte[0]);
            cache.Set(in TestItem.KeccakB.ValueKeccak, new byte[0]);
            cache.Set(in TestItem.KeccakC.ValueKeccak, new byte[0]);

            cache.Set(in TestItem.KeccakA.ValueKeccak, null);

            cache.MemorySize.Should().Be(416);
            cache.Get(in TestItem.KeccakA.ValueKeccak).Should().BeNull();

            cache.Set(in TestItem.KeccakD.ValueKeccak, new byte[0]);
            cache.MemorySize.Should().Be(488);
            cache.Get(in TestItem.KeccakB.ValueKeccak).Should().NotBeNull();
            cache.Get(in TestItem.KeccakD.ValueKeccak).Should().NotBeNull();
        }
    }
}
