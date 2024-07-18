using BenchmarkDotNet.Attributes;
using Nethermind.Core.Specs;
using Nethermind.Specs;

namespace Nethermind.Benchmarks.Core
{
    [MemoryDiagnoser]
    public class SpecBenchmark
    {

        private ISpecProvider _provider;

        [GlobalSetup]
        public void Setup()
        {
            _provider = MainnetSpecProvider.Instance;
        }

        [Benchmark]
        public void WithInheritance()
        {
            var spec = _provider.GetSpec((MainnetSpecProvider.GrayGlacierBlockNumber, MainnetSpecProvider.ShanghaiBlockTimestamp));
            _ = spec.UseTxAccessLists;
        }

        [Benchmark]
        public void WithoutInheritance()
        {
            var spec = _provider.GetSpec((ForkActivation)0L);
            _ = spec.UseTxAccessLists;
        }

    }
}
