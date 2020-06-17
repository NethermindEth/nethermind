using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class RipEmdBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Ripemd160Precompile.Instance;
        protected override string InputsDirectory => "ripemd";
    }
}