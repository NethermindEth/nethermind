using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Snarks;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [MemoryDiagnoser]
    [NativeMemoryProfiler]
    [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    // [DryJob(RuntimeMoniker.NetCoreApp31)]
    public class Bn256AddBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile[] Precompiles => new[]
        {
            EthereumJBn256AddPrecompile.Instance,
            MclBn256AddPrecompile.Instance, 
            ShamatarBn256AddPrecompile.Instance
        };
        
        protected override string InputsDirectory => "bnadd";
    }
}