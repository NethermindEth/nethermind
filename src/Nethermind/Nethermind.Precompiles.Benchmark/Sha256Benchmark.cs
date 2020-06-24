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
    public class Sha256Benchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile[] Precompiles => new[] {Sha256Precompile.Instance};
        protected override string InputsDirectory => "sha256";
    }
}