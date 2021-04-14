using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    // [ShortRunJob(RuntimeMoniker.NetCoreApp50)]
    [SimpleJob(RuntimeMoniker.NetCoreApp50)]
    // [DryJob(RuntimeMoniker.NetCoreApp50)]
    // [MemoryDiagnoser]
    // [NativeMemoryProfiler]
    public class Bn256PairingBenchmark : PrecompileBenchmarkBase
    {
        protected override IEnumerable<IPrecompile> Precompiles => new[]
        {
            Evm.Precompiles.Snarks.Shamatar.Bn256PairingPrecompile.Instance
        };
        
        protected override string InputsDirectory => "bnpair";
    }
}
