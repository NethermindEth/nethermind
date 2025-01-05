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
using Microsoft.Diagnostics.Tracing.Parsers;
using Nethermind.Evm.Tracing.GethStyle;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Collections.Generic;
using Nethermind.Blockchain;
using static Nethermind.Evm.VirtualMachine;
using System.Reflection;
using Nethermind.Core.Test.Builders;
using System.Linq;
using Microsoft.Extensions.Options;
using BenchmarkDotNet.Running;

namespace Nethermind.Evm.Benchmark
{
    public interface ILocalSetup
    {
        void Setup();
        void Run();
        void Reset();
    }
    public struct LocalSetup<TIsTracing, TIsCompiling> : ILocalSetup
        where TIsCompiling : struct, VirtualMachine.IIsOptimizing
        where TIsTracing : struct, VirtualMachine.IIsTracing
    {

        internal delegate CallResult ExecuteCode<TTracingInstructions, TTracingRefunds, TTracingStorage>(EvmState vmState, scoped ref EvmStack<TTracingInstructions> stack, long gasAvailable, IReleaseSpec spec)
            where TTracingInstructions : struct, VirtualMachine.IIsTracing
            where TTracingRefunds : struct, VirtualMachine.IIsTracing
            where TTracingStorage : struct, VirtualMachine.IIsTracing;
        public string Name { get; init; }

        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private VirtualMachine<VirtualMachine.NotTracing, TIsCompiling> _virtualMachine;
        private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
        private EvmState _evmState;
        private WorldState _stateProvider;
        private ILogger _logger;

        public LocalSetup(string name, byte[] bytecode, int? ilvmMode)
        {
            Name = name;


            VMConfig vmConfig = new VMConfig();
            vmConfig.BakeInTracingInAotModes = (typeof(TIsTracing) == typeof(VirtualMachine.IsTracing));

            if (typeof(TIsCompiling) == typeof(VirtualMachine.IsOptimizing))
            {
                vmConfig.AnalysisQueueMaxSize = 1;

                vmConfig.PartialAotThreshold = 0;
                vmConfig.AggressivePartialAotMode = (ilvmMode == ILMode.PARTIAL_AOT_MODE);
                vmConfig.IsPartialAotEnabled = (ilvmMode == ILMode.PARTIAL_AOT_MODE);

                vmConfig.PatternMatchingThreshold = 0;
                vmConfig.IsPatternMatchingEnabled = (ilvmMode == ILMode.PATTERN_BASED_MODE);

                vmConfig.FullAotThreshold= 0;
                vmConfig.IsFullAotEnabled = (ilvmMode == ILMode.FULL_AOT_MODE);
            }

            TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
            IKeyValueStore codeDb = new MemDb();
            _stateProvider = new WorldState(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            _stateProvider.Commit(_spec);
            CodeInfoRepository codeInfoRepository = new();

            ILogManager logmanager = vmConfig.BakeInTracingInAotModes ? LimboLogs.Instance : NullLogManager.Instance;

            _logger = logmanager.GetClassLogger();
            if (vmConfig.BakeInTracingInAotModes)
            {
                _txTracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
            }


            _virtualMachine = new VirtualMachine<VirtualMachine.NotTracing, TIsCompiling>(_blockhashProvider, MainnetSpecProvider.Instance, vmConfig, _logger);

            var codeinfo = new CodeInfo(bytecode, Address.FromNumber(23));

            if (vmConfig.IsPartialAotEnabled || vmConfig.IsPatternMatchingEnabled || vmConfig.IsFullAotEnabled)
            {
                IlAnalyzer.Analyse(codeinfo, ilvmMode.Value, vmConfig, NullLogger.Instance);
            }

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
        }

        public void Setup()
        {
            _evmState = new EvmState(long.MaxValue, _environment, ExecutionType.TRANSACTION, _stateProvider.TakeSnapshot());
            if(_environment.CodeInfo.IlInfo.Mode == ILMode.FULL_AOT_MODE)
            {
                _evmState.ILedContract = Activator.CreateInstance(_environment.CodeInfo.IlInfo.DynamicContractType, [
                    _evmState, _stateProvider, _spec, _blockhashProvider, _txTracer, _logger, _environment.CodeInfo.IlInfo.ContractMetadata.EmbeddedData
                ]) as IPrecompiledContract;
            }

        }

        public void Run()
        {
            _virtualMachine.Run<TIsTracing>(_evmState, _stateProvider, _txTracer);
        }

        public void Reset()
        {
            _stateProvider.Reset();
        }

        public override string ToString()
        {
            return Name;
        }

        public ITxTracer TxTracer => _txTracer;
    }

    [MemoryDiagnoser]
    public class EvmBenchmarks
    {
        public static IEnumerable<ILocalSetup> GetBenchmarkSamples()
        {
            IEnumerable<(string, byte[])> GetBenchmarkSamplesGen(byte[] argBytes)
            {
                yield return ($"Fib With args {new UInt256(argBytes)}", Prepare.EvmCode
                        .PushData(argBytes)
                        .COMMENT("1st/2nd fib number")
                        .PushData(0)
                        .PushData(1)
                        .COMMENT("MAINLOOP:")
                        .JUMPDEST()
                        .DUPx(3)
                        .ISZERO()
                        .PushData(26 + argBytes.Length)
                        .JUMPI()
                        .COMMENT("fib step")
                        .DUPx(2)
                        .DUPx(2)
                        .ADD()
                        .SWAPx(2)
                        .POP()
                        .SWAPx(1)
                        .COMMENT("decrement fib step counter")
                        .SWAPx(2)
                        .PushData(1)
                        .SWAPx(1)
                        .SUB()
                        .SWAPx(2)
                        .PushData(5 + argBytes.Length).COMMENT("goto MAINLOOP")
                        .JUMP()

                        .COMMENT("CLEANUP:")
                        .JUMPDEST()
                        .SWAPx(2)
                        .POP()
                        .POP()
                        .COMMENT("done: requested fib number is the only element on the stack!")
                        .STOP()
                        .Done);

                yield return ($"IsPrime With args {new UInt256(argBytes)}", Prepare.EvmCode
                        .PUSHx(argBytes)
                        .COMMENT("Store variable(n) in Memory")
                        .MSTORE(0)
                        .COMMENT("Store Indexer(i) in Memory")
                        .PushData(2)
                        .MSTORE(32)
                        .COMMENT("We mark this place as a GOTO section")
                        .JUMPDEST()
                        .COMMENT("We check if i * i < n")
                        .MLOAD(32)
                        .DUPx(1)
                        .MUL()
                        .MLOAD(0)
                        .LT()
                        .PushData(47 + argBytes.Length)
                        .JUMPI()
                        .COMMENT("We check if n % i == 0")
                        .MLOAD(32)
                        .MLOAD(0)
                        .MOD()
                        .ISZERO()
                        .DUPx(1)
                        .COMMENT("if 0 we jump to the end")
                        .PushData(51 + argBytes.Length)
                        .JUMPI()
                        .POP()
                        .COMMENT("increment Indexer(i)")
                        .MLOAD(32)
                        .ADD(1)
                        .MSTORE(32)
                        .COMMENT("Loop back to top of conditional loop")
                        .PushData(9 + argBytes.Length)
                        .JUMP()
                        .COMMENT("return 0")
                        .JUMPDEST()
                        .PushData(0)
                        .STOP()
                        .JUMPDEST()
                        .Done);

            }

            UInt256[] args = [1, 23, 101, 4999];

            foreach (var arg in args)
            {
                byte[] bytes = new byte[32];
                arg.ToBigEndian(bytes);
                var argBytes = bytes.WithoutLeadingZeros().ToArray();
                foreach (var bytecode in GetBenchmarkSamplesGen(argBytes))
                {
                    yield return new LocalSetup<NotTracing, NotOptimizing>("ILEVM::std::" + bytecode.Item1, bytecode.Item2, null);
                    yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::pat::" + bytecode.Item1, bytecode.Item2, ILMode.PATTERN_BASED_MODE);
                    yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::jit::" + bytecode.Item1, bytecode.Item2, ILMode.PARTIAL_AOT_MODE);
                    yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::aot::" + bytecode.Item1, bytecode.Item2, ILMode.FULL_AOT_MODE);
                }
            }
        }

        [ParamsSource(nameof(GetBenchmarkSamples))]
        public ILocalSetup BenchmarkSetup;

        [IterationSetup]
        public void Setup()
        {
            BenchmarkSetup.Setup();
        }

        [Benchmark]
        public void ExecuteCode()
        {
            BenchmarkSetup.Run();
        }

        [IterationCleanup]
        public void Cleanup()
        {
            BenchmarkSetup.Reset();
        }
    }


    [MemoryDiagnoser]
    public class CustomEvmBenchmarks()
    {
        public IEnumerable<ILocalSetup> GetBenchmarkSamples()
        {
            byte[] bytecode = Bytes.FromHexString(Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.CODE") ?? string.Empty);
            string BenchmarkName = Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.NAME") ?? string.Empty;

            yield return new LocalSetup<NotTracing, NotOptimizing>("ILEVM::std::" +  BenchmarkName, bytecode, null);
            yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::pat::" + BenchmarkName, bytecode, ILMode.PATTERN_BASED_MODE);
            yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::jit::" + BenchmarkName, bytecode, ILMode.PARTIAL_AOT_MODE);
            yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::aot::" + BenchmarkName, bytecode, ILMode.FULL_AOT_MODE);
        }

        [ParamsSource(nameof(GetBenchmarkSamples))]
        public ILocalSetup BenchmarkSetup;
        
        [IterationSetup]
        public void Setup()
        {
            BenchmarkSetup.Setup();
        }

        [Benchmark]
        public void ExecuteCode()
        {
            BenchmarkSetup.Run();
        }

        [IterationCleanup]
        public void Cleanup()
        {
            BenchmarkSetup.Reset();
        }
    }
}
