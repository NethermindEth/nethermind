using BenchmarkDotNet.Running;

namespace Nethermind.Abi.Benchmark;

public class Program
{
    public static void Main(string[] args) =>
        BenchmarkRunner.Run(typeof(Program).Assembly, args: args);
}
