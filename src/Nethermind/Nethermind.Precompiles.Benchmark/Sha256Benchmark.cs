using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class Sha256Benchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompiledContract Precompile => Sha256PrecompiledContract.Instance;
        protected override string InputsDirectory => "sha256";
    }
}