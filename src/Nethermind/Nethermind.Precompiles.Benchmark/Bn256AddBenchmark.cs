using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Mcl.Bn256;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [MemoryDiagnoser]
    [NativeMemoryProfiler]
    // [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    [DryJob(RuntimeMoniker.NetCoreApp31)]
    public class Bn256AddBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Bn256AddPrecompile.Instance;
        protected override string InputsDirectory => "bnadd";
    }
}