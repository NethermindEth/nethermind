using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class RipEmdBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompiledContract Precompile => Ripemd160PrecompiledContract.Instance;
        protected override string InputsDirectory => "ripemd";
    }
}