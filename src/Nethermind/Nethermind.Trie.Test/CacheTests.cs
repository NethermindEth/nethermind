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
            MemCountingCache cache = new MemCountingCache(1024, string.Empty);
            cache.MemorySize.Should().Be(160);
        }
        
        [Test]
        public void Cache_post_init_memory_calculated_correctly()
        {
            MemCountingCache cache = new MemCountingCache(1024, string.Empty);
            cache.Set(Keccak.Zero, new byte[0]); 
            cache.MemorySize.Should().Be(400);
        }
        
        [Test]
        public void Cache_post_capacity_growth_memory_calculated_correctly()
        {
            MemCountingCache cache = new MemCountingCache(1024, string.Empty);
            cache.Set(TestItem.KeccakA, new byte[0]);
            cache.Set(TestItem.KeccakB, new byte[0]);
            cache.Set(TestItem.KeccakC, new byte[0]);
            cache.Set(TestItem.KeccakD, new byte[0]);
            cache.MemorySize.Should().Be(824);
        }
        
        [Test]
        public void Limit_by_memory_works_fine()
        {
            MemCountingCache cache = new MemCountingCache(800, string.Empty);
            cache.Set(TestItem.KeccakA, new byte[0]);
            cache.Set(TestItem.KeccakB, new byte[0]);
            cache.Set(TestItem.KeccakC, new byte[0]);
            
            cache.MemorySize.Should().Be(608);
            cache.Get(TestItem.KeccakA).Should().NotBeNull();
            
            cache.Set(TestItem.KeccakD, new byte[0]);
            cache.MemorySize.Should().Be(608);
            cache.Get(TestItem.KeccakB).Should().BeNull();
            cache.Get(TestItem.KeccakD).Should().NotBeNull();
            
            cache.Set(TestItem.KeccakE, new byte[0]);
            cache.MemorySize.Should().Be(608);
            cache.Get(TestItem.KeccakB).Should().BeNull();
            cache.Get(TestItem.KeccakC).Should().BeNull();
            cache.Get(TestItem.KeccakE).Should().NotBeNull();
        }
    }
}