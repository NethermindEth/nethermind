using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Nevermind.Core;
using Nevermind.Core.Extensions;

namespace Nevermind.Evm
{
    [DebuggerDisplay("{ExecutionType} to {Env.ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {OutputDestination}:{OutputLength}")]
    public class EvmState
    {
        public readonly byte[][] BytesOnStack = new byte[VirtualMachine.MaxStackSize][];
        public readonly bool[] IntPositions = new bool[VirtualMachine.MaxStackSize];
        public readonly BigInteger[] IntsOnStack = new BigInteger[VirtualMachine.MaxStackSize];

        private HashSet<Address> _destroyList = new HashSet<Address>();
        private List<LogEntry> _logs = new List<LogEntry>();
        public int StackHead = 0;

        public EvmState(long gasAvailable, ExecutionEnvironment env, ExecutionType executionType, bool isContinuation)
            : this(gasAvailable, env, executionType, -1, -1, 0L, 0L, false, isContinuation)
        {
            GasAvailable = gasAvailable;
            Env = env;
        }

        internal EvmState(
            long gasAvailable,
            ExecutionEnvironment env,
            ExecutionType executionType,
            int stateSnapshot,
            int storageSnapshot,
            long outputDestination,
            long outputLength,
            bool isStatic,
            bool isContinuation)
        {
            GasAvailable = gasAvailable;
            ExecutionType = executionType;
            StateSnapshot = stateSnapshot;
            StorageSnapshot = storageSnapshot;
            Env = env;
            OutputDestination = outputDestination;
            OutputLength = outputLength;
            IsStatic = isStatic;
            IsContinuation = isContinuation;
        }

        public ExecutionEnvironment Env { get; }
        public long GasAvailable { get; set; }
        public BigInteger ProgramCounter { get; set; }

        internal ExecutionType ExecutionType { get; }
        internal long OutputDestination { get; }
        internal long OutputLength { get; }
        public bool IsStatic { get; }
        public bool IsContinuation { get; set; }
        public int StateSnapshot { get; }
        public int StorageSnapshot { get; }

        public long Refund { get; set; }
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