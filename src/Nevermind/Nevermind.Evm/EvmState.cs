using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Nevermind.Core;

namespace Nevermind.Evm
{
    [DebuggerDisplay("{ExecutionType} to {Env.ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {OutputDestination}:{OutputLength}")]
    public class EvmState
    {
        public readonly byte[][] BytesOnStack = new byte[VirtualMachine.MaxStackSize][];
        public readonly bool[] IntPositions = new bool[VirtualMachine.MaxStackSize];
        public readonly BigInteger[] IntsOnStack = new BigInteger[VirtualMachine.MaxStackSize];

        private ulong _activeWordsInMemory;
        private HashSet<Address> _destroyList = new HashSet<Address>();
        private List<LogEntry> _logs = new List<LogEntry>();
        public int StackHead = 0;

        // TODO - snapshot 0, not -1 I guess
        public EvmState(ulong gasAvailable, ExecutionEnvironment env, ExecutionType executionType, bool isContinuation)
            : this(gasAvailable, env, executionType, -1, -1, BigInteger.Zero, BigInteger.Zero, isContinuation)
        {
            GasAvailable = gasAvailable;
            Env = env;
        }

        internal EvmState(
            ulong gasAvailable,
            ExecutionEnvironment env,
            ExecutionType executionType,
            int stateSnapshot,
            int storageSnapshot,
            BigInteger outputDestination,
            BigInteger outputLength,
            bool isContinuation)
        {
            GasAvailable = gasAvailable;
            ExecutionType = executionType;
            StateSnapshot = stateSnapshot;
            StorageSnapshot = storageSnapshot;
            Env = env;
            OutputDestination = outputDestination;
            OutputLength = outputLength;
            IsContinuation = isContinuation;
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
                if (ShouldLog.Evm)
                {
                    Console.WriteLine($"  MEMORY SIZE REMAINS {_activeWordsInMemory}");
                }

                _activeWordsInMemory = value;
            }
        }

        internal ExecutionType ExecutionType { get; }
        internal BigInteger OutputDestination { get; }
        internal BigInteger OutputLength { get; }
        public bool IsContinuation { get; set; }
        public int StateSnapshot { get; }
        public int StorageSnapshot { get; }

        public BigInteger Refund { get; set; } = BigInteger.Zero;
        public EvmMemory Memory { get; } = new EvmMemory();

        public HashSet<Address> DestroyList
        {
            get { return LazyInitializer.EnsureInitialized(ref _destroyList, () => new HashSet<Address>()); }
        }

        public List<LogEntry> Logs
        {
            get { return LazyInitializer.EnsureInitialized(ref _logs, () => new List<LogEntry>()); }
        }
    }
}