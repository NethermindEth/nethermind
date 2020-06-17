using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Benchmark
{
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    [MemoryDiagnoser]
    public class Sha256Benchmark
    {
        private SHA256 _system = SHA256.Create();

        private byte[] _bytes = new byte[32]
        {
            1,2,3,4,5,6,7,8,
            1,2,3,4,5,6,7,8,
            1,2,3,4,5,6,7,8,
            1,2,3,4,5,6,7,8,
        };

        [Benchmark(Baseline = true)]
        public byte[] Current()
        {
            return Sha256.ComputeBytes(_bytes);
        }

        [Benchmark]
        public byte[] New()
        {
            return _system.ComputeHash(_bytes);
        }
    }
}
