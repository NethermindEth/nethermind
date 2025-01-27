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
using Nethermind.Specs.Forks;
using Nethermind.Abi;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;

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

        private readonly ICodeInfoRepository codeInfoRepository;
        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private VirtualMachine<VirtualMachine.NotTracing, TIsCompiling> _virtualMachine;
        private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
        private EvmState _evmState;
        private WorldState _stateProvider;
        private ILogger _logger;
        private byte[] bytecode;
        private VMConfig vmConfig;
        private int? mode;
        private IlAnalyzer _ilAnalyzer;
        private CodeInfo driverCodeInfo;
        public LocalSetup(string name, byte[] _bytecode, int? ilvmMode)
        {
            Name = name;

            vmConfig = new VMConfig();
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

            bytecode = _bytecode;
            mode = ilvmMode;

            codeInfoRepository = new CodeInfoRepository();  

            ILogManager logmanager = vmConfig.BakeInTracingInAotModes ? LimboLogs.Instance : NullLogManager.Instance;

            _logger = logmanager.GetClassLogger();
            if (vmConfig.BakeInTracingInAotModes)
            {
                _txTracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
            }

            _virtualMachine = new VirtualMachine<VirtualMachine.NotTracing, TIsCompiling>(_blockhashProvider, codeInfoRepository, MainnetSpecProvider.Instance, vmConfig, _logger);

            _ilAnalyzer = new IlAnalyzer(_stateProvider, MainnetSpecProvider.Instance, _blockhashProvider, codeInfoRepository);

            var address = InsertCode(bytecode);

            var driver =
                Prepare.EvmCode
                .COMMENT("BEGIN")
                .Call(address, 1000000)
                .POP()
                .COMMENT("END")
                .STOP()
                .Done;

            var driverCodeinfo = new CodeInfo(driver, Address.FromNumber(23));
            var targetCodeInfo = codeInfoRepository.GetCachedCodeInfo(_stateProvider, address, Prague.Instance);

            if (vmConfig.IsPartialAotEnabled || vmConfig.IsPatternMatchingEnabled || vmConfig.IsFullAotEnabled)
            {
                _ilAnalyzer.Analyse(driverCodeinfo, mode.Value, vmConfig, NullLogger.Instance);
                _ilAnalyzer.Analyse(targetCodeInfo, mode.Value, vmConfig, NullLogger.Instance);
            }

            driverCodeInfo = driverCodeinfo;
        }
        private Address InsertCode(byte[] bytecode, Address target = null)
        {
            var hashcode = Keccak.Compute(bytecode);
            var address = target ?? new Address(hashcode);

            var spec = Prague.Instance;
            _stateProvider.CreateAccount(address, 1_000_000_000);
            _stateProvider.InsertCode(address, bytecode, spec);
            return address;
        }

        public void Setup()
        {
            _environment = new ExecutionEnvironment
            (
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: driverCodeInfo,
                value: 0,
                transferValue: 0,
                txExecutionContext: new TxExecutionContext(_header, Address.Zero, 0, null, codeInfoRepository),
                inputData: default
            );

            _evmState = new EvmState(long.MaxValue, _environment, ExecutionType.TRANSACTION, _stateProvider.TakeSnapshot());
        }

        public void Run()
        {
            _virtualMachine.Run<TIsTracing>(_evmState, _stateProvider, _txTracer);
        }

        public void Reset()
        {
            //_stateProvider.Reset();
        }

        public override string ToString()
        {
            return Name;
        }

        public ITxTracer TxTracer => _txTracer;
    }

    [MemoryDiagnoser]
    public class EvmBenchmarks()
    {
        static byte[] fibbBytecode(byte[] argBytes) => Prepare.EvmCode
                        .JUMPDEST()
                        .PUSHx([0, 0])
                        .POP()

                        .PushData(argBytes)
                        .COMMENT("1st/2nd fib number")
                        .PushData(0)
                        .PushData(1)
                        .COMMENT("MAINLOOP:")
                        .JUMPDEST()
                        .DUPx(3)
                        .ISZERO()
                        .PushData(5 + 26 + argBytes.Length)
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
                        .PushData(5 + 5 + argBytes.Length).COMMENT("goto MAINLOOP")
                        .JUMP()

                        .COMMENT("CLEANUP:")
                        .JUMPDEST()
                        .SWAPx(2)
                        .POP()
                        .POP()
                        .COMMENT("done: requested fib number is the only element on the stack!")
                        .STOP()
                        .Done;

        static byte[] isPrimeBytecode(byte[] argBytes) => Prepare.EvmCode
                        .JUMPDEST()
                        .PUSHx([0])
                        .POP()

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
                        .PushData(4 + 47 + argBytes.Length)
                        .JUMPI()
                        .COMMENT("We check if n % i == 0")
                        .MLOAD(32)
                        .MLOAD(0)
                        .MOD()
                        .ISZERO()
                        .DUPx(1)
                        .COMMENT("if 0 we jump to the end")
                        .PushData(4 + 51 + argBytes.Length)
                        .JUMPI()
                        .POP()
                        .COMMENT("increment Indexer(i)")
                        .MLOAD(32)
                        .ADD(1)
                        .MSTORE(32)
                        .COMMENT("Loop back to top of conditional loop")
                        .PushData(4 + 9 + argBytes.Length)
                        .JUMP()
                        .COMMENT("return 0")
                        .JUMPDEST()
                        .PushData(0)
                        .STOP()
                        .JUMPDEST()
                        .Done;
        public static IEnumerable<ILocalSetup> GetBenchmarkSamples()
        {
            int mode = Int32.Parse(Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.MODE") ?? string.Empty);

            UInt256[] f_args = [1, 23, 101, 1023, 2047, 4999];
            UInt256[] p_args = [1, 23, 1023, 8000009, 16000057];

            foreach (var arg in f_args)
            {
                byte[] bytes = new byte[32];
                arg.ToBigEndian(bytes);
                var argBytes = bytes.WithoutLeadingZeros().ToArray();
                var bytecode = fibbBytecode(argBytes);

                string benchName = $"Fib With args {new UInt256(argBytes)}";

                if (mode.HasFlag(1))
                {
                    yield return new LocalSetup<NotTracing, NotOptimizing>("ILEVM::1::std::" + benchName, bytecode, null);
                }

                if (mode.HasFlag(2))
                {
                    yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::2::pat::" + benchName, bytecode, ILMode.PATTERN_BASED_MODE);
                }

                if (mode.HasFlag(4))
                {
                    yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::3::jit::" + benchName, bytecode, ILMode.PARTIAL_AOT_MODE);
                }

                if (mode.HasFlag(8))
                {
                    yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::4::aot::" + benchName, bytecode, ILMode.FULL_AOT_MODE);
                }
            }


            foreach (var arg in p_args)
            {
                byte[] bytes = new byte[32];
                arg.ToBigEndian(bytes);
                var argBytes = bytes.WithoutLeadingZeros().ToArray();

                string benchName = $"Prim With args {new UInt256(argBytes)}";
                var bytecode = isPrimeBytecode(argBytes);

                if (mode.HasFlag(1))
                {
                    yield return new LocalSetup<NotTracing, NotOptimizing>("ILEVM::1::std::" + benchName, bytecode, null);
                }

                if (mode.HasFlag(2))
                {
                    yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::2::pat::" + benchName, bytecode, ILMode.PATTERN_BASED_MODE);
                }

                if (mode.HasFlag(4))
                {
                    yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::3::jit::" + benchName, bytecode, ILMode.PARTIAL_AOT_MODE);
                }

                if (mode.HasFlag(8))
                {
                    yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::4::aot::" + benchName, bytecode, ILMode.FULL_AOT_MODE);
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
            int mode = Int32.Parse(Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.MODE") ?? string.Empty);
            string BenchmarkName = Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE.NAME") ?? string.Empty;
            if (mode.HasFlag(1))
            {
                yield return new LocalSetup<NotTracing, NotOptimizing>("ILEVM::1::std::" + BenchmarkName, bytecode, null);
            }

            if (mode.HasFlag(2))
            {
                yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::2::pat::" + BenchmarkName, bytecode, ILMode.PATTERN_BASED_MODE);
            }

            if (mode.HasFlag(4))
            {
                yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::3::jit::" + BenchmarkName, bytecode, ILMode.PARTIAL_AOT_MODE);
            }

            if (mode.HasFlag(8))
            {
                yield return new LocalSetup<NotTracing, IsOptimizing>("ILEVM::4::aot::" + BenchmarkName, bytecode, ILMode.FULL_AOT_MODE);
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
}
