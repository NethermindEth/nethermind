using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    public class Sha256Benchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile Precompile => Sha256Precompile.Instance;
        protected override string InputsDirectory => "sha256";
    }
}