using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [NativeMemoryProfiler]
    [MemoryDiagnoser]
    [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    public class RipEmdBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile[] Precompiles => new[] {Ripemd160Precompile.Instance};
        protected override string InputsDirectory => "ripemd";
    }
}