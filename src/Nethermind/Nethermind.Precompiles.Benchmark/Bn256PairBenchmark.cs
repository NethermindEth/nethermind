using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using BenchmarkDotNet.Jobs;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Snarks;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [ShortRunJob(RuntimeMoniker.NetCoreApp31)]
    // [DryJob(RuntimeMoniker.NetCoreApp31)]
    [MemoryDiagnoser]
    // [NativeMemoryProfiler]
    public class Bn256PairingBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile[] Precompiles => new[]
        {
#pragma warning disable 618
            EthereumJBn256PairingPrecompile.Instance,
#pragma warning restore 618
            MclBn256PairingPrecompile.Instance, 
            ShamatarBn256PairingPrecompile.Instance
        };
        
        protected override string InputsDirectory => "bnpair";
    }
}