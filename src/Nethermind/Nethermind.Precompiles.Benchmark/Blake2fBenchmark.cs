using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class Blake2fBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompiledContract Precompile => Blake2BPrecompiledContract.Instance;
        protected override string InputsDirectory => "blake2f";
    }
}