using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    // [DryJob(RuntimeMoniker.NetCoreApp31)]
    [MemoryDiagnoser]
    public class Bn256PairingBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Bn256PairingPrecompile.Instance;
        protected override string InputsDirectory => "bnmul";
    }
}