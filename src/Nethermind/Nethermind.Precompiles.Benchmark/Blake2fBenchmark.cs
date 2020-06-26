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
    public class Blake2fBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile[] Precompiles => new[] {Blake2FPrecompile.Instance};
        protected override string InputsDirectory => "blake2f";
    }
}