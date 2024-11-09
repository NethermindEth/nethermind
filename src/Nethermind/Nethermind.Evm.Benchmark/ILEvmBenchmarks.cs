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
using Nethermind.Evm.CodeAnalysis.IL;
using Microsoft.Diagnostics.Runtime;
using Nethermind.Evm.Config;

namespace Nethermind.Evm.Benchmark
{
    [MemoryDiagnoser]
    public class ILEvmBenchmarks
    {
        public static byte[] ByteCode { get; set; }

        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private IVirtualMachine _virtualMachine;
        private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
        private EvmState _evmState;
        private WorldState _stateProvider;

        [GlobalSetup]
        public void GlobalSetup()
        {
            ByteCode = Bytes.FromHexString(Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE") ?? string.Empty);
            Console.WriteLine($"Running benchmark for bytecode {ByteCode?.ToHexString()}");

            TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
            IKeyValueStore codeDb = new MemDb();
            _stateProvider = new WorldState(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            _stateProvider.Commit(_spec);
            CodeInfoRepository codeInfoRepository = new();
            _virtualMachine = new VirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, codeInfoRepository, LimboLogs.Instance);

            VMConfig vMConfig = new VMConfig
            {
                BakeInTracingInJitMode = false,
                JittingThreshold = 1,
                AggressiveJitMode = true,
                IsJitEnabled = true,
                AnalysisQueueMaxSize = 1,
                PatternMatchingThreshold = int.MaxValue,
                IsPatternMatchingEnabled = false,
            };

            var codeinfo = new CodeInfo(ByteCode);

            IlAnalyzer.Analyse(codeinfo, 2, vMConfig, NullLogger.Instance);

            _environment = new ExecutionEnvironment
            (
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: codeinfo,
                value: 0,
                transferValue: 0,
                txExecutionContext: new TxExecutionContext(_header, Address.Zero, 0, null, codeInfoRepository),
                inputData: default
            );

            _evmState = new EvmState(long.MaxValue, _environment, ExecutionType.TRANSACTION, _stateProvider.TakeSnapshot());
        }

        [Benchmark]
        public void ExecuteCode()
        {
            _virtualMachine.Run<VirtualMachine.NotTracing>(_evmState, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }
    }
}
