using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    // [DryJob(RuntimeMoniker.NetCoreApp31)]
    [MemoryDiagnoser]
    public class Bn256MulBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Bn256MulPrecompile.Instance;
        protected override string InputsDirectory => "bnmul";
    }
}