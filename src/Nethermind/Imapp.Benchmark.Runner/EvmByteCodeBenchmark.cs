using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Evm;
using Nethermind.Evm.Benchmark;

namespace Imapp.Benchmark.Runner
{
    [MaxWarmupCount(10)]
    [MaxIterationCount(40)]
    [MemoryDiagnoser]
    public class EvmByteCodeBenchmark
    {
        public static byte[] ByteCode { get; set; }

        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.IstanbulBlockNumber);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private IVirtualMachine _virtualMachine;
        private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, UInt256.One, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();
        private EvmState _evmState;
        private StateProvider _stateProvider;
        private StorageProvider _storageProvider;
        private WorldState _worldState;

        [GlobalSetup]
        public void GlobalSetup()
        {
            ByteCode = Bytes.FromHexString(Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE") ?? string.Empty);
            //Console.WriteLine($"Running benchmark for bytecode {ByteCode?.ToHexString()}");

            TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
            IKeyValueStore codeDb = new MemDb();

            _stateProvider = new StateProvider(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            _stateProvider.Commit(_spec);

            _storageProvider = new StorageProvider(trieStore, _stateProvider, new OneLoggerLogManager(NullLogger.Instance));

            _worldState = new WorldState(_stateProvider, _storageProvider);

            _virtualMachine = new VirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, LimboLogs.Instance);

            _environment = new ExecutionEnvironment
            {
                ExecutingAccount = Address.Zero,
                CodeSource = Address.Zero,
                Caller = Address.Zero,
                CodeInfo = new CodeInfo(ByteCode),
                Value = 0,
                TransferValue = 0,
                TxExecutionContext = new TxExecutionContext(_header, Address.Zero, 0)
            };
        }

        [IterationSetup]
        public void Setup()
        {
            _evmState = new EvmState(long.MaxValue, _environment, ExecutionType.Transaction, true, _worldState.TakeSnapshot(), false);
        }

        [Benchmark]
        public void ExecuteCode()
        {
            _virtualMachine.Run(_evmState, _worldState, _txTracer);
            _stateProvider.Reset();
            _storageProvider.Reset();
        }
    }
}
