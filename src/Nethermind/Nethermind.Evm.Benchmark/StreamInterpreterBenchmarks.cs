// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

        // Straight-line dispatcher-like glue (no jumps): 100 repetitions of a fusable
        // arithmetic/dup/swap pattern with PUSH20 masking — the node-workload-shaped profile.
        private static readonly byte[] StraightLine = BuildStraightLine();

        private static byte[] BuildStraightLine()
        {
            List<byte> code = [(byte)Instruction.PUSH1, 0x55];
            for (int i = 0; i < 100; i++)
            {
                code.AddRange([
                    (byte)Instruction.DUP1,
                    (byte)Instruction.PUSH1, 0x07,
                    (byte)Instruction.MUL,
                    (byte)Instruction.PUSH1, 0x03,
                    (byte)Instruction.ADD,
                    (byte)Instruction.DUP2,
                    (byte)Instruction.XOR,
                    (byte)Instruction.PUSH1, 0x01,
                    (byte)Instruction.SHR,
                    (byte)Instruction.SWAP1,
                    (byte)Instruction.POP,
                ]);
                code.Add((byte)Instruction.PUSH20);
                code.AddRange(new byte[20]);
                code[^11] = 0xFF;
                code.Add((byte)Instruction.AND);
            }

            code.Add((byte)Instruction.STOP);
            return [.. code];
        }

        private ExecutionEnvironment _straightLineEnvironment;

        private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.OsakaActivation);
        private readonly ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private IVirtualMachine _virtualMachine;
        private readonly IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();
        private IWorldState _stateProvider;
        private IDisposable _stateScope;

        [Params(InterpreterMode.ByteCodeLoop, InterpreterMode.Stream)]
        public InterpreterMode Mode { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        {
            StreamInterpreter.Enabled = Mode != InterpreterMode.ByteCodeLoop;

            BlockHeader header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One,
                MainnetSpecProvider.ParisBlockNumber + 4, Int64.MaxValue,
                MainnetSpecProvider.OsakaBlockTimestamp, Bytes.Empty);

            _stateProvider = TestWorldStateFactory.CreateForTest();
            _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether);
            _stateProvider.Commit(_spec);
            _stateProvider.CommitTree(0);
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

            _straightLineEnvironment = ExecutionEnvironment.Rent(
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: new CodeInfo(StraightLine),
                callDepth: 0,
                value: 0,
                inputData: default
            );

        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            _environment.Dispose();
            _straightLineEnvironment.Dispose();
            _stateScope.Dispose();
            StreamInterpreter.Enabled = Environment.GetEnvironmentVariable("NETHERMIND_EVM_STREAM") == "1";
        }

        [Benchmark]
        public void ExecuteStraightLine()
        {
            using VmState<EthereumGasPolicy> evmState = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromLong(100_000_000), ExecutionType.TRANSACTION, _straightLineEnvironment, new StackAccessTracker(), _stateProvider.TakeSnapshot());
            _virtualMachine.ExecuteTransaction<OffFlag>(evmState, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }

        [Benchmark]
        public void ExecuteComputeLoop()
        {
            // A fresh frame per invocation: a reused VmState resumes at the end of code and
            // measures nothing. The rent cost is identical across modes and amortized over
            // ~14k executed ops.
            using VmState<EthereumGasPolicy> evmState = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromLong(100_000_000), ExecutionType.TRANSACTION, _environment, new StackAccessTracker(), _stateProvider.TakeSnapshot());
            _virtualMachine.ExecuteTransaction<OffFlag>(evmState, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }
    }
}
