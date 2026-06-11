// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.Evm.Benchmark
{
    /// <summary>
    /// Compares the bytecode loop against the stream interpreter (with and without const
    /// fusion) on a glue-heavy compute loop — the local proxy for the eth_call workload, so
    /// interpreter changes are measured in minutes instead of node deploy cycles.
    /// </summary>
    [MemoryDiagnoser]
    public class StreamInterpreterBenchmarks
    {
        public enum InterpreterMode
        {
            ByteCodeLoop,
            Stream,
            StreamFused,
        }

        // 1024 iterations of arithmetic/dup/swap glue with fusable PUSH1+op pairs and a
        // PUSH2+JUMPI loop head — no storage, so the interpreter dominates entirely.
        private static readonly byte[] s_computeLoop =
        [
            (byte)Instruction.PUSH2, 0x04, 0x00,
            (byte)Instruction.JUMPDEST,           // pc 3: loop head
            (byte)Instruction.PUSH1, 0x01,
            (byte)Instruction.SWAP1,
            (byte)Instruction.SUB,
            (byte)Instruction.DUP1,
            (byte)Instruction.PUSH1, 0x07,
            (byte)Instruction.MUL,
            (byte)Instruction.PUSH1, 0x03,
            (byte)Instruction.ADD,
            (byte)Instruction.DUP2,
            (byte)Instruction.XOR,
            (byte)Instruction.PUSH1, 0x01,
            (byte)Instruction.SHR,
            (byte)Instruction.POP,
            (byte)Instruction.DUP1,
            (byte)Instruction.PUSH2, 0x00, 0x03,
            (byte)Instruction.JUMPI,
            (byte)Instruction.STOP,
        ];

        private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.OsakaActivation);
        private readonly ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private IVirtualMachine _virtualMachine;
        private readonly IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();
        private VmState<EthereumGasPolicy> _evmState;
        private IWorldState _stateProvider;

        [Params(InterpreterMode.ByteCodeLoop, InterpreterMode.Stream, InterpreterMode.StreamFused)]
        public InterpreterMode Mode { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        {
            StreamInterpreter.Enabled = Mode != InterpreterMode.ByteCodeLoop;
            StreamInterpreter.FusionEnabled = Mode == InterpreterMode.StreamFused;

            BlockHeader header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One,
                MainnetSpecProvider.ParisBlockNumber + 4, Int64.MaxValue,
                MainnetSpecProvider.OsakaBlockTimestamp, Bytes.Empty);

            _stateProvider = TestWorldStateFactory.CreateForTest();
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether);
            EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
            _virtualMachine = new EthereumVirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, LimboLogs.Instance);
            _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(header, _spec));
            _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

            // Fresh CodeInfo per mode so the stream is built under this mode's fusion flag.
            _environment = ExecutionEnvironment.Rent(
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: new CodeInfo(s_computeLoop),
                callDepth: 0,
                value: 0,
                inputData: default
            );

            _evmState = VmState<EthereumGasPolicy>.RentTopLevel(EthereumGasPolicy.FromLong(long.MaxValue), ExecutionType.TRANSACTION, _environment, new StackAccessTracker(), _stateProvider.TakeSnapshot());
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            _evmState.Dispose();
            _environment.Dispose();
            StreamInterpreter.Enabled = Environment.GetEnvironmentVariable("NETHERMIND_EVM_STREAM") == "1";
            StreamInterpreter.FusionEnabled = Environment.GetEnvironmentVariable("NETHERMIND_EVM_STREAM_FUSION") == "1";
        }

        [Benchmark]
        public void ExecuteComputeLoop()
        {
            _virtualMachine.ExecuteTransaction<OffFlag>(_evmState, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }
    }
}
