using BenchmarkDotNet.Running;

namespace Nethermind.Abi.Benchmarks;

public class Program
{
    public static void Main(string[] args) =>
        BenchmarkRunner.Run(typeof(Program).Assembly, args: args);
}
