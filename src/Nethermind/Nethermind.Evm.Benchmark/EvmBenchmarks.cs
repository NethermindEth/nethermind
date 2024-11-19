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

namespace Nethermind.Evm.Benchmark
{
    public class LocalSetup
    {
        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation) MainnetSpecProvider.IstanbulBlockNumber);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private IVirtualMachine _virtualMachine;
        private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
        private EvmState _evmState;
        private WorldState _stateProvider;

        public LocalSetup(bool isIlvmOn, byte[] bytecode)
        {
            VMConfig vmConfig = isIlvmOn ? new VMConfig
            {
                BakeInTracingInJitMode = false,
                JittingThreshold = int.MaxValue,
                AggressiveJitMode = true,
                IsJitEnabled = true,
                AnalysisQueueMaxSize = 1,
                PatternMatchingThreshold = int.MaxValue,
                IsPatternMatchingEnabled = false,
            } : new VMConfig();

            TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
            IKeyValueStore codeDb = new MemDb();
            _stateProvider = new WorldState(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            _stateProvider.Commit(_spec);
            CodeInfoRepository codeInfoRepository = new();
            _virtualMachine = new VirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, codeInfoRepository, LimboLogs.Instance, vmConfig);

            var codeinfo = new CodeInfo(bytecode);

            if(isIlvmOn)
            {
                IlAnalyzer.Analyse(codeinfo, 2, vmConfig, NullLogger.Instance);
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

            _evmState = new EvmState(long.MaxValue, _environment, ExecutionType.TRANSACTION, _stateProvider.TakeSnapshot());
        }

        public void Run()
        {
            _virtualMachine.Run<VirtualMachine.NotTracing>(_evmState, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }
    }

    [MemoryDiagnoser]
    public class EvmBenchmarks
    {
        private LocalSetup ilvmSetup;
        private LocalSetup normalSetup;

        [GlobalSetup]
        public void GlobalSetup()
        {

            var ByteCode =
                Prepare.EvmCode
                    .PUSHx([(byte)0x01, (byte)0x00, (byte)0x00, (byte)0x00])
                    .COMMENT("1st/2nd fib number")
                    .PushData(0)
                    .PushData(1)
                    .COMMENT("MAINLOOP:")
                    .JUMPDEST()
                    .DUPx(3)
                    .ISZERO()
                    .PushData(30)
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
                    .PushData(9).COMMENT("goto MAINLOOP")
                    .JUMP()

                    .COMMENT("CLEANUP:")
                    .JUMPDEST()
                    .SWAPx(2)
                    .POP()
                    .POP()
                    .COMMENT("done: requested fib number is the only element on the stack!")
                    .STOP()
                    .Done; 
            Console.WriteLine($"Running benchmark for bytecode {ByteCode?.ToHexString()}");

            normalSetup = new LocalSetup(false, ByteCode);
            ilvmSetup = new LocalSetup(true, ByteCode);
        }

        [Benchmark(Baseline = true)]
        public void ExecuteCode_Normal()
        {
            normalSetup.Run();
        }

        [Benchmark]
        public void ExecuteCode_Ilevm()
        {
            ilvmSetup.Run();
        }
    }
}
