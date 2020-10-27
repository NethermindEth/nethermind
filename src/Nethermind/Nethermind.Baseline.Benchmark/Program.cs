using BenchmarkDotNet.Running;

namespace Nethermind.Baseline.Benchmark
{
    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run(typeof(Program).Assembly);
        }
    }
}
