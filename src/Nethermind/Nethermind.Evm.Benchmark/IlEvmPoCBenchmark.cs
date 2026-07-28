// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
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
    /// Task 25 §3.1 go/no-go PoC for the IL-EVM (compile hot bytecode) direction.
    ///
    /// Compares the real interpreter executing a straight-line arithmetic bytecode against the
    /// equivalent compiled form: the same chain of UInt256 operations held in locals, with the
    /// block's summed static gas charged once per block — exactly what an EVM→IL basic-block
    /// compiler would emit (RyuJIT compiles hand-written C# and emitted IL identically, so this
    /// measures the performance ceiling without the Reflection.Emit plumbing).
    ///
    /// The repeated 9-op block is a dependence chain (acc -> 6*acc + 37), so neither side can
    /// dead-code-eliminate it, and both sides see the same operand values throughout.
    ///
    /// Decision rule from the task: compiled ≥3× faster → proceed with IL-EVM; &lt;2× → stop.
    /// </summary>
    [MemoryDiagnoser]
    public class IlEvmPoCBenchmark
    {
        private const int BlockCount = 1800;
        // PUSH1 + ADD + PUSH1 + MUL + DUP1 + PUSH1 + SWAP1 + SUB + ADD = 3+3+3+5+3+3+3+3+3
        private const long BlockStaticGas = 29;
        private const byte Seed = 9;

        private static readonly UInt256 SevenValue = 7;
        private static readonly UInt256 ThreeValue = 3;
        private static readonly UInt256 FiveValue = 5;

        private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        private readonly ITxTracer _txTracer = NullTxTracer.Instance;
        private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private readonly IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();

        private ExecutionEnvironment _environment;
        private IVirtualMachine _virtualMachine;
        private IWorldState _stateProvider;
        private IDisposable _worldStateScope;
        private UInt256 _sink;

        [GlobalSetup]
        public void GlobalSetup()
        {
            byte[] byteCode = BuildBytecode();

            _stateProvider = TestWorldStateFactory.CreateForTest();
            _worldStateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether);
            _stateProvider.Commit(_spec);
            EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
            _virtualMachine = new EthereumVirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, LimboLogs.Instance);
            _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(_header, _spec));
            _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

            _environment = ExecutionEnvironment.Rent(
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: new CodeInfo(byteCode),
                callDepth: 0,
                value: 0,
                inputData: default
            );

        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            _environment.Dispose();
            _worldStateScope.Dispose();
        }

        [Benchmark(Baseline = true)]
        public void Interpreter()
        {
            // A fresh frame per invocation: VmState keeps its program counter, so reusing one
            // across invocations would re-enter with pc at code end and execute nothing.
            using VmState<EthereumGasPolicy> evmState = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromLong(long.MaxValue), ExecutionType.TRANSACTION, _environment, new StackAccessTracker(), _stateProvider.TakeSnapshot());
            _virtualMachine.ExecuteTransaction<OffFlag>(evmState, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }

        [Benchmark]
        public UInt256 CompiledBlocks()
        {
            UInt256 acc = Seed;
            long gas = long.MaxValue;
            for (int i = 0; i < BlockCount; i++)
            {
                gas -= BlockStaticGas;
                if (gas < 0) ThrowOutOfGas();

                UInt256.Add(in acc, in SevenValue, out UInt256 sum);     // PUSH1 7; ADD
                UInt256.Multiply(in sum, in ThreeValue, out UInt256 m);  // PUSH1 3; MUL
                UInt256.Subtract(in m, in FiveValue, out UInt256 d);     // DUP1; PUSH1 5; SWAP1; SUB
                UInt256.Add(in m, in d, out acc);                        // ADD
            }
            _sink = acc;
            return acc;
        }

        /// <summary>
        /// One prologue push, then <see cref="BlockCount"/> copies of the 9-op dependence-chain
        /// block <c>acc → 2*(3*(acc+7)) - 5</c>, then STOP.
        /// </summary>
        private static byte[] BuildBytecode()
        {
            const int BlockBytes = 12;
            byte[] code = new byte[2 + BlockCount * BlockBytes + 1];
            int i = 0;
            code[i++] = (byte)Instruction.PUSH1;
            code[i++] = Seed;
            for (int block = 0; block < BlockCount; block++)
            {
                code[i++] = (byte)Instruction.PUSH1;
                code[i++] = 7;
                code[i++] = (byte)Instruction.ADD;
                code[i++] = (byte)Instruction.PUSH1;
                code[i++] = 3;
                code[i++] = (byte)Instruction.MUL;
                code[i++] = (byte)Instruction.DUP1;
                code[i++] = (byte)Instruction.PUSH1;
                code[i++] = 5;
                code[i++] = (byte)Instruction.SWAP1;
                code[i++] = (byte)Instruction.SUB;
                code[i++] = (byte)Instruction.ADD;
            }
            code[i] = (byte)Instruction.STOP;
            return code;
        }

        [DoesNotReturn]
        private static void ThrowOutOfGas() => throw new InvalidOperationException("Out of gas");
    }
}
