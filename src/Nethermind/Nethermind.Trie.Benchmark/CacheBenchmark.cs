using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Benchmark
{
    [MemoryDiagnoser]
    [DryJob]
    public class CacheBenchmark
    {
        private static readonly ValueHash256 KeccakA = ValueKeccak.Compute("A");
        private static readonly ValueHash256 KeccakB = ValueKeccak.Compute("B");
        private static readonly ValueHash256 KeccakC = ValueKeccak.Compute("C");
        private static readonly ValueHash256 KeccakD = ValueKeccak.Compute("D");

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
        public MemCountingCache Pre_init_trie_cache()
        {
            MemCountingCache memCountingCache
                = new(MemorySizes.MiB, string.Empty);
            return memCountingCache;
        }

        [Benchmark]
        public MemCountingCache Post_init_trie_cache_with_item()
        {
            MemCountingCache cache
                = new(MemorySizes.MiB, string.Empty);
            cache.Set(ValueKeccak.Zero, Array.Empty<byte>());
            return cache;
        }

        [Benchmark]
        public MemCountingCache With_2_items_cache()
        {
            MemCountingCache cache
                = new(MemorySizes.MiB, string.Empty);
            cache.Set(KeccakA, Array.Empty<byte>());
            cache.Set(KeccakB, Array.Empty<byte>());
            return cache;
        }

        [Benchmark]
        public MemCountingCache With_3_items_cache()
        {
            MemCountingCache cache
                = new(MemorySizes.MiB, string.Empty);
            cache.Set(KeccakA, Array.Empty<byte>());
            cache.Set(KeccakB, Array.Empty<byte>());
            cache.Set(KeccakC, Array.Empty<byte>());
            return cache;
        }

        [Benchmark]
        public MemCountingCache Post_dictionary_growth_cache()
        {
            MemCountingCache cache
                = new(MemorySizes.MiB, string.Empty);
            cache.Set(KeccakA, Array.Empty<byte>());
            cache.Set(KeccakB, Array.Empty<byte>());
            cache.Set(KeccakC, Array.Empty<byte>());
            cache.Set(KeccakD, Array.Empty<byte>());
            return cache;
        }
    }
}
