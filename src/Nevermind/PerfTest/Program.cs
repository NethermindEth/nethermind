using System;
using System.Diagnostics;
using Ethereum.VM.Test;
using Nevermind.Core.Encoding;
using Nevermind.Evm;
using Nevermind.Store;

namespace PerfTest
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            VirtualMachine machine = new VirtualMachine();
            ExecutionEnvironment env = new ExecutionEnvironment();
            const string callData =
                "0x2839e92800000000000000000000000000000000000000000000000000000000000000030000000000000000000000000000000000000000000000000000000000000002";
            const string ackermann =
                "0x60e060020a6000350480632839e92814601e57806361047ff414603457005b602a6004356024356047565b8060005260206000f35b603d6004356099565b8060005260206000f35b600082600014605457605e565b8160010190506093565b81600014606957607b565b60756001840360016047565b90506093565b609060018403608c85600186036047565b6047565b90505b92915050565b6000816000148060a95750816001145b60b05760b7565b81905060cf565b60c1600283036099565b60cb600184036099565b0190505b91905056";
            env.MachineCode = Hex.ToBytes(ackermann);
            env.InputData = Hex.ToBytes(callData);
            InMemoryDb db = new InMemoryDb();
            StateTree stateTree = new StateTree(db);

            const int iterations = 10000;
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            BlockhashProvider blockhashProvider = new BlockhashProvider();
            WorldStateProvider worldStateProvider = new WorldStateProvider(stateTree);
            TestStorageProvider storageProvider = new TestStorageProvider(db);
            for (int i = 0; i < iterations; i++)
            {
                machine.Run(
                    env,
                    new EvmState(1_000_000_000L), 
                    null,
                    null,
                    null);
            }
            stopwatch.Stop();

            long ns = 1_000_000_000L * stopwatch.ElapsedTicks / Stopwatch.Frequency;
            Console.WriteLine("ON AVERAGE (ns): " + ns / iterations);
            Console.ReadLine();
        }
    }
}