using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using BenchmarkDotNet.Jobs;
using Nethermind.Core.Crypto;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    // [NativeMemoryProfiler]
    // [MemoryDiagnoser]
    // [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class KeccakBenchmark
    {
        public readonly struct Param
        {
            private static Random _random = new Random(42);
            
            public Param(byte[] bytes)
            {
                Bytes = bytes;
                _random.NextBytes(Bytes);
            }
            
            public byte[] Bytes { get; }

            public override string ToString()
            {
                return $"bytes[{Bytes.Length.ToString().PadLeft(4, '0')}]";
            }
        }
        
        public IEnumerable<Param> Inputs 
        {
            get
            {
                for (int i = 0; i <= 512; i += 4)
                {
                    yield return new Param(new byte[i]);    
                }
            }
        }

        [ParamsSource(nameof(Inputs))]
        public Param Input { get; set; }

        [Benchmark(Baseline = true)]
        public Span<byte> Baseline()
        {
            return ValueKeccak.Compute(Input.Bytes).BytesAsSpan;
        }
    }
}