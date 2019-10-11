using BenchmarkDotNet.Running;

namespace Nethermind.Perfshop
{
    class Program
    {
        static void Main(string[] args)
        {
//            BenchmarkRunner.Run<BloomsBenchmark>();
//            BenchmarkRunner.Run<SwapBytesBenchmark>();
//            BenchmarkRunner.Run<Int256Benchmark>();
//            BenchmarkRunner.Run<ReverseBytesBenchmark>();
            BenchmarkRunner.Run<Blake2Benchmark>();
//            BenchmarkRunner.Run<SwapBytesBenchmark>();
        }
    }
}