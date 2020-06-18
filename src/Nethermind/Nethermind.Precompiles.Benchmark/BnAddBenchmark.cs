using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [MemoryDiagnoser]
    // [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    [DryJob(RuntimeMoniker.NetCoreApp31)]
    public class BnAddBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Bn128AddPrecompile.Instance;
        protected override string InputsDirectory => "bnadd";
    }
}