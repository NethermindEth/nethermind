// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

namespace Nethermind.Benchmark.Bytecode
{
    public class PrecompilesBytecodeDirect
    {
        public static byte[] ByteCode { get; set; }

        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((MainnetSpecProvider.GrayGlacierBlockNumber, MainnetSpecProvider.ShardingForkBlockTimestamp));
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private IVirtualMachine _virtualMachine;
        private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.GrayGlacierBlockNumber, Int64.MaxValue, MainnetSpecProvider.ShardingForkBlockTimestamp, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();
        private EvmState _evmState;
        private StateProvider _stateProvider;
        private StorageProvider _storageProvider;
        private WorldState _worldState;

        public void Setup()
        {
            //PointEvaluationBenchmark | pointEvaluation1 | 50000 gas cost ns
            ByteCode = Bytes.FromHexString("7f013c03613f6fc558fb7e61e75602241ed9a2f04e36d8670aadd286e71b5ca9cc610000527f4200000000000000000000000000000000000000000000000000000000000000610020527f31e5a2356cbc2ef6a733eae8d54bf48719ae3d990017ca787c419c7d369f8e3c610040527f83fac17c3f237fc51f90e2c660eb202a438bc2025baded5cd193c1a018c5885b610060527fc9281ba704d5566082e851235c7be763b2a99adff965e0a121ee972ebc472d02610080527f944a74f5c6243e14052e105124b70bf65faf85ad3a494325e269fad097842cba6100a0526020600060c06000600060145af15000");

            Console.WriteLine($"Running benchmark for bytecode {ByteCode?.ToHexString()}");

            TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
            IKeyValueStore codeDb = new MemDb();

            _stateProvider = new StateProvider(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            _stateProvider.Commit(_spec);

            _storageProvider = new StorageProvider(trieStore, _stateProvider, new OneLoggerLogManager(NullLogger.Instance));

            _worldState = new WorldState(_stateProvider, _storageProvider);

            _virtualMachine = new VirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, new OneLoggerLogManager(NullLogger.Instance));

            _environment = new ExecutionEnvironment
            {
                ExecutingAccount = Address.Zero,
                CodeSource = Address.Zero,
                Caller = Address.Zero,
                CodeInfo = new CodeInfo(ByteCode),
                Value = 0,
                TransferValue = 0,
                TxExecutionContext = new TxExecutionContext(_header, Address.Zero, 0, Array.Empty<byte[]>())
            };

            _evmState = new EvmState(long.MaxValue, _environment, ExecutionType.Transaction, true, _worldState.TakeSnapshot(), false);
        }

        public void ExecuteCode()
        {
            _evmState = new EvmState(long.MaxValue, _environment, ExecutionType.Transaction, true, _worldState.TakeSnapshot(), false);
            var ts = _virtualMachine.Run(_evmState, _worldState, _txTracer);

            if (ts.IsError)
            {
                throw new Exception("Execution failed:" + ts.Error);
            }

            _stateProvider.Reset();
            _storageProvider.Reset();
        }
    }
}
