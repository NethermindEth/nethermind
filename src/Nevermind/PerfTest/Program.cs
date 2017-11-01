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
            ShouldLog.Evm = false;
            InMemoryDb db = new InMemoryDb();
            StateTree stateTree = new StateTree(db);
            StorageTree storageTree = new StorageTree(db);

            Stopwatch stopwatch = new Stopwatch();
            for (int i = 0; i < 100; i++)
            {
                EvmState state = new EvmState(1000000000);
                machine.Run(
                    env,
                    state,
                    new BlockhashProvider(),
                    new WorldStateProvider(stateTree),
                    new TestStorageProvider(db));
            }

            Console.WriteLine(stopwatch.ElapsedMilliseconds / 100);
            Console.ReadLine();
        }
    }
}