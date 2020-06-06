using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class BnAddBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompiledContract Precompile => Bn128AddPrecompiledContract.Instance;
        protected override string InputsDirectory => "bnadd";
    }
}