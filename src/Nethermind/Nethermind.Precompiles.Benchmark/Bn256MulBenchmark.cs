using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    // [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    // [DryJob(RuntimeMoniker.NetCoreApp31)]
    // [MemoryDiagnoser]
    // [NativeMemoryProfiler]
    public class Bn256MulBenchmark : PrecompileBenchmarkBase
    {
        protected override IEnumerable<IPrecompile> Precompiles => new[]
        {
            Evm.Precompiles.Snarks.Shamatar.Bn256MulPrecompile.Instance
        };
        
        protected override string InputsDirectory => "bnmul";
    }
}