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
using static Microsoft.FSharp.Core.ByRefKinds;
using System.Collections.Generic;
using Nethermind.Serialization.Json;
using System.IO;
using System.Linq;

namespace Nethermind.Benchmark.Bytecode
{
    public class PrecompilesBytecodeBenchmark
    {
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

        [GlobalSetup]
        public void GlobalSetup()
        {
            TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
            IKeyValueStore codeDb = new MemDb();

            _stateProvider = new StateProvider(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            _stateProvider.Commit(_spec);

            _storageProvider = new StorageProvider(trieStore, _stateProvider, new OneLoggerLogManager(NullLogger.Instance));

            _worldState = new WorldState(_stateProvider, _storageProvider);

            _virtualMachine = new VirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, new OneLoggerLogManager(NullLogger.Instance));
        }

        [IterationSetup]
        public void Setup()
        {
            _environment = new ExecutionEnvironment
            {
                ExecutingAccount = Address.Zero,
                CodeSource = Address.Zero,
                Caller = Address.Zero,
                CodeInfo = new CodeInfo(Input.Bytecode),
                Value = 0,
                TransferValue = 0,
                TxExecutionContext = new TxExecutionContext(_header, Address.Zero, 0, Array.Empty<byte[]>())
            };
            _evmState = new EvmState(long.MaxValue, _environment, ExecutionType.Transaction, true, _worldState.TakeSnapshot(), false);
        }

        [Benchmark]
        public void ExecuteCode()
        {
            var ts = _virtualMachine.Run(_evmState, _worldState, _txTracer);
            if (ts.IsError)
            {
                throw new Exception("Execution failed: " + ts.Error);
            }
        }

        [IterationCleanup]
        public void Cleanup()
        {
            _stateProvider.Reset();
            _storageProvider.Reset();
        }

        [ParamsSource(nameof(Inputs))]
        public BenchmarkParam Input { get; set; }

        public IEnumerable<BenchmarkParam> Inputs
        {
            get
            {
                var inputs = new List<BenchmarkParam>();
                foreach (string file in Directory.GetFiles($"bytecodes/precompiles", "*.json", SearchOption.TopDirectoryOnly))
                {
                    EthereumJsonSerializer jsonSerializer = new EthereumJsonSerializer();
                    var jsonInputs = jsonSerializer.Deserialize<BenchmarkParam[]>(File.ReadAllText(file));
                    inputs.AddRange(jsonInputs);
                }

                return inputs;
            }
        }
    }
}
