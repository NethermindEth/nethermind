// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Evm.Benchmark
{
    [MemoryDiagnoser]
    public class EvmBenchmarks
    {
        public static byte[] ByteCode { get; set; }

        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private IVirtualMachine _virtualMachine;
        private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
        private EvmState _evmState;
        private IWorldState _stateProvider;

        [GlobalSetup]
        public void GlobalSetup()
        {
            ByteCode = Bytes.FromHexString(Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE") ?? string.Empty);
            Console.WriteLine($"Running benchmark for bytecode {ByteCode?.ToHexString()}");

            IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
            _stateProvider = worldStateManager.GlobalWorldState;
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            _stateProvider.Commit(_spec);
            CodeInfoRepository codeInfoRepository = new();
            _virtualMachine = new VirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, LimboLogs.Instance);
            _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(_header, _spec));
            _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

            _environment = new ExecutionEnvironment
            (
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: new CodeInfo(ByteCode),
                callDepth: 0,
                value: 0,
                transferValue: 0,
                inputData: default
            );

            _evmState = EvmState.RentTopLevel(long.MaxValue, ExecutionType.TRANSACTION, _environment, new StackAccessTracker(), _stateProvider.TakeSnapshot());
        }

        [Benchmark]
        public void ExecuteCode()
        {
            _virtualMachine.ExecuteTransaction<OffFlag>(_evmState, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }
    }
}
