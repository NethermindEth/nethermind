using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Precompiles.Benchmark
{
    [HtmlExporter]
    [MemoryDiagnoser]
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    [SuppressMessage("ReSharper", "UnassignedGetOnlyAutoProperty")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    public class Sha256Benchmark
    {
        private IPrecompiledContract _precompile = Sha256PrecompiledContract.Instance;

        [GlobalSetup]
        public void GlobalSetup()
        {

        }
        
        public IEnumerable<byte[]> Inputs 
        {
            get
            {
                foreach (var file in Directory.GetFiles("sha256/current", "*.csv", SearchOption.TopDirectoryOnly))
                {
                    byte[][] inputs = File.ReadAllLines(file)
                        .Select(LineToTestInput).ToArray();

                    foreach (var input in inputs)
                    {
                        yield return input;
                    }
                }
            }
        }

        [ParamsSource(nameof(Inputs))]
        public byte[] Input { get; set; }

        private static byte[] LineToTestInput(string line)
        {
            return Bytes.FromHexString(line.Split(',')[0]);
        }

        [Benchmark(Baseline = true)]
        public (byte[], bool) Baseline()
        {
            return _precompile.Run(Input);
        }
    }
}