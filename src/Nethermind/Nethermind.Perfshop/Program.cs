using System;
using BenchmarkDotNet.Running;

namespace Nethermind.Perfshop
{
    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run<SwapBytes>();
            BenchmarkRunner.Run<Int256Benchmark>();
            BenchmarkRunner.Run<ReverseBytesBenchmark>();
        }
    }
}