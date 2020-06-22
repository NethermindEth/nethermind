using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Mcl.Bn256;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    [MemoryDiagnoser]
    // [DryJob(RuntimeMoniker.NetCoreApp31)]
    public class BnPairBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Bn128PairingPrecompile.Instance;
        protected override string InputsDirectory => "bnpair";
    }
}