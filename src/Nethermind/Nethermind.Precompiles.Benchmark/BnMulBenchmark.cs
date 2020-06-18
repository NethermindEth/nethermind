using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [MemoryDiagnoser]
    // [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    [DryJob(RuntimeMoniker.NetCoreApp31)]
    public class BnMulBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Bn128MulPrecompile.Instance;
        protected override string InputsDirectory => "bnmul";
    }
}