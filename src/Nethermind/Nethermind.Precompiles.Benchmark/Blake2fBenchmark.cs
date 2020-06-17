using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class Blake2fBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Blake2FPrecompile.Instance;
        protected override string InputsDirectory => "blake2f";
    }
}