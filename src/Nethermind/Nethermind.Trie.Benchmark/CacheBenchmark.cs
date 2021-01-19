using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Trie.Benchmark
{
    [MemoryDiagnoser]
    [DryJob(RuntimeMoniker.NetCoreApp31)]
    public class CacheBenchmark
    {
        // public readonly struct Param
        // {
        //     public Param(byte[] bytes)
        //     {
        //         Bytes = bytes;
        //     }
        //     
        //     public byte[] Bytes { get; }
        //
        //     public override string ToString()
        //     {
        //         return $"bytes[{Bytes.Length.ToString().PadLeft(4, '0')}]";
        //     }
        // }
        //
        // public IEnumerable<Param> Inputs 
        // {
        //     get
        //     {
        //         yield return new Param(new byte[0]);
        //         yield return new Param(new byte[32]);
        //         yield return new Param(new byte[64]);
        //         yield return new Param(new byte[96]);
        //         yield return new Param(new byte[128]);
        //         yield return new Param(new byte[1024]);
        //         yield return new Param(new byte[2048]);
        //     }
        // }
        //
        // [ParamsSource(nameof(Inputs))]
        // public Param Input { get; set; }

        [Benchmark]
        public MemCountingCache Pre_init_trie_cache_160()
        {
            MemCountingCache memCountingCache
                = new MemCountingCache(1024 * 1024, string.Empty);
            return memCountingCache;
        }

        [Benchmark]
        public MemCountingCache Post_init_trie_cache_with_item_400()
        {
            MemCountingCache cache
                = new MemCountingCache(1024 * 1024, string.Empty);
            cache.Set(Keccak.Zero, new byte[0]);
            return cache;
        }

        [Benchmark]
        public MemCountingCache With_2_items_cache_504()
        {
            MemCountingCache cache
                = new MemCountingCache(1024 * 1024, string.Empty);
            cache.Set(TestItem.KeccakA, new byte[0]);
            cache.Set(TestItem.KeccakB, new byte[0]);
            return cache;
        }
        
        [Benchmark]
        public MemCountingCache With_3_items_cache_608()
        {
            MemCountingCache cache
                = new MemCountingCache(1024 * 1024, string.Empty);
            cache.Set(TestItem.KeccakA, new byte[0]);
            cache.Set(TestItem.KeccakB, new byte[0]);
            cache.Set(TestItem.KeccakC, new byte[0]);
            return cache;
        }
        
        [Benchmark]
        public MemCountingCache Post_dictionary_growth_cache_824_and_136_lost()
        {
            MemCountingCache cache
                = new MemCountingCache(1024 * 1024, string.Empty);
            cache.Set(TestItem.KeccakA, new byte[0]);
            cache.Set(TestItem.KeccakB, new byte[0]);
            cache.Set(TestItem.KeccakC, new byte[0]);
            cache.Set(TestItem.KeccakD, new byte[0]);
            return cache;
        }
    }
}