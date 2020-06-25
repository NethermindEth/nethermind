using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    // [DryJob(RuntimeMoniker.NetCoreApp31)]
    [MemoryDiagnoser]
    // [NativeMemoryProfiler]
    public class Bn256MulBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile[] Precompiles => new[]
        {
#pragma warning disable 618
            Evm.Precompiles.Snarks.EthereumJ.Bn256MulPrecompile.Instance,
#pragma warning restore 618
            Evm.Precompiles.Snarks.Mcl.Bn256MulPrecompile.Instance, 
            Evm.Precompiles.Snarks.Shamatar.Bn256MulPrecompile.Instance
        };
        
        protected override string InputsDirectory => "bnmul";
    }
}