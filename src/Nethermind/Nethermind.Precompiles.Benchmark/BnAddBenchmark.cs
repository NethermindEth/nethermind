using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class BnAddBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Bn256AddPrecompile.Instance;
        protected override string InputsDirectory => "bnadd";
    }
}