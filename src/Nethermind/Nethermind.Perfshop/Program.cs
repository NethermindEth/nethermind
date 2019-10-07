using BenchmarkDotNet.Running;

namespace Nethermind.Perfshop
{
    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run<SumBenchmark>();
//            BenchmarkRunner.Run<Blake2Benchmark>();
//            BenchmarkRunner.Run<SwapBytes>();
//            BenchmarkRunner.Run<Int256Benchmark>();
//            BenchmarkRunner.Run<ReverseBytesBenchmark>();
        }
    }
}