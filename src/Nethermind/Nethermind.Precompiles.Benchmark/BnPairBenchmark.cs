using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class BnPairBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompiledContract Precompile => Bn128PairingPrecompiledContract.Instance;
        protected override string InputsDirectory => "bnpair";
    }
}