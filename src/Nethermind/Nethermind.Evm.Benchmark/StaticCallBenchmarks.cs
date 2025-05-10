// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Evm.Benchmark
{
    [MemoryDiagnoser]
    public class StaticCallBenchmarks
    {
        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private IVirtualMachine _virtualMachine;
        private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.MuirGlacierBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
        private EvmState _evmState;
        private WorldState _stateProvider;

        public IEnumerable<byte[]> Bytecodes
        {
            get
            {
                yield return bytecode1;
                yield return bytecode2;
            }
        }

        byte[] bytecode1 = Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(0)
            .Op(Instruction.DUP1)
            .Op(Instruction.DUP1)
            .Op(Instruction.DUP1)
            .PushData(4)
            .Op(Instruction.GAS)
            .Op(Instruction.STATICCALL)
            .Op(Instruction.POP)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done;

        byte[] bytecode2 = Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(0)
            .Op(Instruction.DUP1)
            .Op(Instruction.DUP1)
            .Op(Instruction.DUP1)
            .PushData(4)
            .Op(Instruction.GAS)
            .Op(Instruction.POP)
            .Op(Instruction.POP)
            .Op(Instruction.POP)
            .Op(Instruction.POP)
            .Op(Instruction.POP)
            .Op(Instruction.POP)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done;

        [ParamsSource(nameof(Bytecodes))]
        public byte[] Bytecode { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
            IKeyValueStoreWithBatching codeDb = new MemDb();

            _stateProvider = new WorldState(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            _stateProvider.Commit(_spec);

            Console.WriteLine(MuirGlacier.Instance);
            CodeInfoRepository codeInfoRepository = new();
            _virtualMachine = new VirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, new OneLoggerLogManager(NullLogger.Instance));

            _environment = new ExecutionEnvironment
            (
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: new CodeInfo(Bytecode),
                value: 0,
                transferValue: 0,
                txExecutionContext: new TxExecutionContext(new BlockExecutionContext(_header, _spec), Address.Zero, 0, null, codeInfoRepository),
                inputData: default
            );

            _evmState = EvmState.RentTopLevel(100_000_000L, ExecutionType.TRANSACTION, _stateProvider.TakeSnapshot(), _environment, new StackAccessTracker());
        }

        [Benchmark(Baseline = true)]
        public void ExecuteCode()
        {
            _virtualMachine.ExecuteTransaction<OffFlag>(_evmState, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }

        [Benchmark]
        public void ExecuteCodeNoTracing()
        {
            _virtualMachine.ExecuteTransaction<OffFlag>(_evmState, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }

        [Benchmark]
        public void No_machine_running()
        {
            _stateProvider.Reset();
        }
    }
}
