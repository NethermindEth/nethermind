﻿using BenchmarkDotNet.Attributes;
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
    public class Bn256AddBenchmark : PrecompileBenchmarkBase
    {
        protected override IPrecompile[] Precompiles => new[]
        {
#pragma warning disable 618
            Evm.Precompiles.Snarks.EthereumJ.Bn256AddPrecompile.Instance,
#pragma warning restore 618
            Evm.Precompiles.Snarks.Mcl.Bn256AddPrecompile.Instance,
            Evm.Precompiles.Snarks.Shamatar.Bn256AddPrecompile.Instance
        };

        protected override string InputsDirectory => "bnadd";
    }
}