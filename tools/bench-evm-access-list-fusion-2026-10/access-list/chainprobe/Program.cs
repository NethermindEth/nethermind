using System.Diagnostics;
using Nethermind.Evm.Benchmark;

// chainprobe CHAIN[,CHAIN...] ROUNDS : ns per executed opcode of OpcodeChainBenchmarks.ExecuteContract, median of ROUNDS x 1 s.
string[] chains = args[0].Split(',');
int rounds = args.Length > 1 ? int.Parse(args[1]) : 7;
const int opcodes = 3846;
foreach (string chain in chains)
{
    OpcodeChainBenchmarks b = new() { Chain = chain, Cancelable = false };
    b.Setup();
    List<double> r = [];
    for (int k = 0; k < rounds; k++)
    {
        long n = 0; Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1000) { for (int i = 0; i < 64; i++) b.ExecuteContract(); n += 64; }
        r.Add(sw.Elapsed.TotalNanoseconds / (n * opcodes));
    }
    b.Cleanup();
    r.Sort();
    Console.WriteLine($"{chain,-18} {r[rounds / 2]:F3} ns/op  (min {r[0]:F3}, max {r[^1]:F3})");
}
