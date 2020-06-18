using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    // [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    [MemoryDiagnoser]
    [DryJob(RuntimeMoniker.NetCoreApp31)]
    public class BnPairBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Bn128PairingPrecompile.Instance;
        protected override string InputsDirectory => "bnpair";
    }
}