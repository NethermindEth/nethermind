using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Mcl.Bn256;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    // [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    [NativeMemoryProfiler]
    [DryJob(RuntimeMoniker.NetCoreApp31)]
    [MemoryDiagnoser]
    public class Bn256PairingBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Bn256PairingPrecompile.Instance;
        protected override string InputsDirectory => "bnpair";
    }
}