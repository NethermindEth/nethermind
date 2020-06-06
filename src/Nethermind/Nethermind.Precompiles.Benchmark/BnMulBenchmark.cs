using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class BnMulBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompiledContract Precompile => Bn128AddPrecompiledContract.Instance;
        protected override string InputsDirectory => "bnmul";
    }
}