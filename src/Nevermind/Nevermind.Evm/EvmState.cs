using System;
using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class EvmState
    {
        public EvmState(ulong gasAvailable, ExecutionEnvironment env)
        {
            GasAvailable = gasAvailable;
            Env = env;
        }

        private ulong _activeWordsInMemory;

        public ulong GasAvailable { get; set; }
        public BigInteger ProgramCounter { get; set; }

        public ulong ActiveWordsInMemory
        {
            get => _activeWordsInMemory;
            set
            {
                if (value != _activeWordsInMemory && ShouldLog.Evm)
                {
                    Console.WriteLine($"  MEMORY SIZE CHANGED FROM {_activeWordsInMemory} TO {value}");
                }
                else if (ShouldLog.Evm)
                {
                    Console.WriteLine($"  MEMORY SIZE REMAINS {_activeWordsInMemory}");
                }

                _activeWordsInMemory = value;
            }
        }

        public EvmMemory Memory { get; } = new EvmMemory();

        public readonly byte[][] BytesOnStack = new byte[VirtualMachine.MaxStackSize][];
        public readonly BigInteger[] IntsOnStack = new BigInteger[VirtualMachine.MaxStackSize];
        public readonly bool[] IntPositions = new bool[VirtualMachine.MaxStackSize];
        public int StackHead = 0;
        public readonly ExecutionEnvironment Env;
        public HashSet<Address> DestroyList = new HashSet<Address>();
        public List<LogEntry> Logs = new List<LogEntry>();
        public BigInteger Refund { get; set; } = BigInteger.Zero;

        public StateSnapshot StateSnapshot { get; set; }
        public StateSnapshot StorageSnapshot { get; set; }

        public bool IsCreate { get; set; }
    }
}