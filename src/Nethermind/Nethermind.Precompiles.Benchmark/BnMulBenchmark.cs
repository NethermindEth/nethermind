using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Mcl.Bn256;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [MemoryDiagnoser]
    [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    // [DryJob(RuntimeMoniker.NetCoreApp31)]
    public class BnMulBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Bn128MulPrecompile.Instance;
        protected override string InputsDirectory => "bnmul";
    }
}