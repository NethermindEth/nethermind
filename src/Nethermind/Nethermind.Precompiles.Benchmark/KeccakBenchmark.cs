using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using BenchmarkDotNet.Jobs;
using Nethermind.Core.Crypto;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [NativeMemoryProfiler]
    [MemoryDiagnoser]
    [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    public class KeccakBenchmark
    {
        public readonly struct Param
        {
            public Param(byte[] bytes)
            {
                Bytes = bytes;
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
                yield return new Param(new byte[0]);
                yield return new Param(new byte[32]);
                yield return new Param(new byte[64]);
                yield return new Param(new byte[96]);
                yield return new Param(new byte[128]);
                yield return new Param(new byte[1024]);
                yield return new Param(new byte[2048]);
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