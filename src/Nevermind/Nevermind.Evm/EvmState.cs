using System;
using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class EvmState
    {
        public readonly byte[][] BytesOnStack = new byte[VirtualMachine.MaxStackSize][];
        public readonly bool[] IntPositions = new bool[VirtualMachine.MaxStackSize];
        public readonly BigInteger[] IntsOnStack = new BigInteger[VirtualMachine.MaxStackSize];

        private ulong _activeWordsInMemory;
        public int StackHead = 0;

        public EvmState(ulong gasAvailable, ExecutionEnvironment env)
            : this(gasAvailable, env, ExecutionType.TransactionLevel, null, null)
        {
            GasAvailable = gasAvailable;
            Env = env;
        }

        internal EvmState(ulong gasAvailable, ExecutionEnvironment env, ExecutionType executionType,
            StateSnapshot stateSnapshot, StateSnapshot storageSnapshot)
        {
            GasAvailable = gasAvailable;
            ExecutionType = executionType;
            StateSnapshot = stateSnapshot;
            StorageSnapshot = storageSnapshot;
            Env = env;
        }

        public ExecutionEnvironment Env { get; }
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

        internal ExecutionType ExecutionType { get; }
        public StateSnapshot StateSnapshot { get; }
        public StateSnapshot StorageSnapshot { get; }

        public BigInteger Refund { get; set; } = BigInteger.Zero;
        public EvmMemory Memory { get; } = new EvmMemory();
        public HashSet<Address> DestroyList { get; } = new HashSet<Address>();
        public List<LogEntry> Logs { get; } = new List<LogEntry>();
    }
}