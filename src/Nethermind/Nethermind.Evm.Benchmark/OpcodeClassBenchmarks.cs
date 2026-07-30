// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
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
    /// Cost of one dispatch, per opcode class, inside the stream interpreter. Each workload runs the
    /// same counted loop twice - once with an empty body, once with a body of <see cref="BodyOps"/>
    /// copies of the class under test - so subtracting the two timings cancels the loop's own six
    /// ops and the harness overhead, and dividing by the body op count yields nanoseconds per
    /// dispatch for that class alone. The interpreter's aggregate figure (total time over total
    /// opcodes on a real workload) cannot separate a push from a 256-bit divide; this can, which is
    /// what decides whether the remaining gap lives in stack traffic, in arithmetic, or in dispatch.
    /// </summary>
    [MemoryDiagnoser]
    public class OpcodeClassBenchmarks
    {
        public enum OpClass
        {
            /// <summary>Baseline: loop with nothing in it. Its time is the subtrahend for the rest.</summary>
            Empty,
            PushPop,
            Dup1Pop,
            Swap1Swap1,
            AddPushed,
            MulPushed,
            LtPushed,
            IsZeroTwice,
            AndPushed,
            MStoreMLoad,
            /// <summary>Two independent ISZEROs, to price the op without the dependency chain the
            /// back-to-back variant serializes through the stack slot.</summary>
            IsZeroIndependent,
            /// <summary>Full-width operands, the shape the pool math uses; the small-value variants
            /// above cannot show what a real 256-bit divide or modmul costs.</summary>
            DivWide,
            MulModWide,
            ExpSmall,
            Keccak32,
            SLoadRepeat,
        }

        private const int Iterations = 20_000;
        private const int BodyOps = 20;

        private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.OsakaActivation);
        private readonly ITxTracer _txTracer = NullTxTracer.Instance;
        private readonly IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();
        private ExecutionEnvironment _environment;
        private IVirtualMachine _virtualMachine;
        private IWorldState _stateProvider;
        private IDisposable _stateScope;

        [ParamsAllValues]
        public OpClass Code { get; set; }

        /// <summary>
        /// Stack-neutral pairs, so the loop's counter stays exactly one slot below the body's
        /// working set and the body can repeat without drift. Operand pushes are part of the pair
        /// being measured: an ADD in real code arrives with its operands pushed, and pricing it
        /// without them would measure an operation no bytecode performs.
        /// </summary>
        private static byte[] BodyFor(OpClass opClass) => opClass switch
        {
            OpClass.Empty => [],
            OpClass.PushPop => [(byte)Instruction.PUSH1, 0x07, (byte)Instruction.POP],
            OpClass.Dup1Pop => [(byte)Instruction.DUP1, (byte)Instruction.POP],
            OpClass.Swap1Swap1 => [(byte)Instruction.PUSH1, 0x07, (byte)Instruction.SWAP1, (byte)Instruction.SWAP1, (byte)Instruction.POP],
            OpClass.AddPushed => [(byte)Instruction.PUSH1, 0x07, (byte)Instruction.PUSH1, 0x09, (byte)Instruction.ADD, (byte)Instruction.POP],
            OpClass.MulPushed => [(byte)Instruction.PUSH1, 0x07, (byte)Instruction.PUSH1, 0x09, (byte)Instruction.MUL, (byte)Instruction.POP],
            OpClass.LtPushed => [(byte)Instruction.PUSH1, 0x07, (byte)Instruction.PUSH1, 0x09, (byte)Instruction.LT, (byte)Instruction.POP],
            OpClass.IsZeroTwice => [(byte)Instruction.DUP1, (byte)Instruction.ISZERO, (byte)Instruction.ISZERO, (byte)Instruction.POP],
            OpClass.AndPushed => [(byte)Instruction.PUSH1, 0x07, (byte)Instruction.PUSH1, 0x09, (byte)Instruction.AND, (byte)Instruction.POP],
            OpClass.MStoreMLoad => [(byte)Instruction.PUSH1, 0x07, (byte)Instruction.PUSH1, 0x20, (byte)Instruction.MSTORE, (byte)Instruction.PUSH1, 0x20, (byte)Instruction.MLOAD, (byte)Instruction.POP],
            OpClass.IsZeroIndependent =>
            [
                (byte)Instruction.PUSH1, 0x07, (byte)Instruction.ISZERO, (byte)Instruction.POP,
                (byte)Instruction.PUSH1, 0x09, (byte)Instruction.ISZERO, (byte)Instruction.POP,
            ],
            OpClass.DivWide => [.. WidePush(), .. WidePush(), (byte)Instruction.DIV, (byte)Instruction.POP],
            OpClass.MulModWide => [.. WidePush(), .. WidePush(), .. WidePush(), (byte)Instruction.MULMOD, (byte)Instruction.POP],
            OpClass.ExpSmall => [(byte)Instruction.PUSH1, 0x20, (byte)Instruction.PUSH1, 0x03, (byte)Instruction.EXP, (byte)Instruction.POP],
            OpClass.Keccak32 => [(byte)Instruction.PUSH1, 0x20, (byte)Instruction.PUSH1, 0x00, (byte)Instruction.KECCAK256, (byte)Instruction.POP],
            OpClass.SLoadRepeat => [(byte)Instruction.PUSH1, 0x01, (byte)Instruction.SLOAD, (byte)Instruction.POP],
            _ => throw new ArgumentOutOfRangeException(nameof(opClass)),
        };

        /// <summary>A PUSH32 of a wide, odd constant: no small-value or zero shortcut is available.</summary>
        private static byte[] WidePush() =>
        [
            (byte)Instruction.PUSH32,
            0xc5, 0xa3, 0x08, 0xf2, 0xcd, 0xf6, 0xf5, 0xa2, 0x1f, 0x12, 0x3b, 0xb5, 0xe3, 0xb4, 0xa6, 0xc7,
            0x8d, 0x9e, 0x0f, 0x1a, 0x2b, 0x3c, 0x4d, 0x5f, 0x25, 0x45, 0xf4, 0x91, 0x4f, 0x6c, 0xdd, 0x1d,
        ];

        /// <summary>Ops the body contributes per iteration, counted as the bytecode loop would.</summary>
        public static int BodyOpCount(OpClass opClass) => opClass switch
        {
            OpClass.Empty => 0,
            OpClass.PushPop => 2,
            OpClass.Dup1Pop => 2,
            OpClass.Swap1Swap1 => 4,
            OpClass.AddPushed or OpClass.MulPushed or OpClass.LtPushed or OpClass.AndPushed => 4,
            OpClass.IsZeroTwice => 4,
            OpClass.MStoreMLoad => 6,
            OpClass.IsZeroIndependent => 6,
            OpClass.DivWide => 4,
            OpClass.MulModWide => 5,
            OpClass.ExpSmall or OpClass.Keccak32 => 4,
            OpClass.SLoadRepeat => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(opClass)),
        };

        public static int TotalBodyOps(OpClass opClass) => BodyOpCount(opClass) * BodyOps * Iterations;

        private static byte[] BuildLoop(OpClass opClass)
        {
            List<byte> code =
            [
                (byte)Instruction.PUSH3, unchecked((byte)(Iterations >> 16)), unchecked((byte)(Iterations >> 8)), unchecked((byte)Iterations),
                (byte)Instruction.JUMPDEST,
            ];
            int loopHead = 4;

            byte[] body = BodyFor(opClass);
            for (int i = 0; i < BodyOps; i++)
            {
                code.AddRange(body);
            }

            // counter--, then jump back while it is non-zero.
            code.AddRange([(byte)Instruction.PUSH1, 0x01, (byte)Instruction.SWAP1, (byte)Instruction.SUB, (byte)Instruction.DUP1]);
            code.AddRange([(byte)Instruction.PUSH2, unchecked((byte)(loopHead >> 8)), unchecked((byte)loopHead), (byte)Instruction.JUMPI]);
            code.Add((byte)Instruction.STOP);
            return code.ToArray();
        }

        [GlobalSetup]
        public void GlobalSetup()
        {
            StreamInterpreter.Enabled = true;
            StreamInterpreter.ForceAllContexts = true;
            StreamInterpreter.BuildThreshold = 1;

            byte[] code = BuildLoop(Code);

            BlockHeader header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One,
                MainnetSpecProvider.ParisBlockNumber + 4, Int64.MaxValue,
                MainnetSpecProvider.OsakaBlockTimestamp, Bytes.Empty);

            _stateProvider = TestWorldStateFactory.CreateForTest(null, NullLogManager.Instance);
            _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether);
            _stateProvider.InsertCode(Address.Zero, Keccak.Compute(code), code, _spec);
            _stateProvider.Commit(_spec);
            _stateProvider.CommitTree(0);

            EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
            _virtualMachine = new EthereumVirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, NullLogManager.Instance);
            _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(header, _spec));
            _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

            _environment = ExecutionEnvironment.Rent(
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                // The stream cache is keyed by code hash and a default hash makes the build mark the
                // CodeInfo permanently unavailable, so the frame would silently run the bytecode loop.
                codeInfo: new CodeInfo(code) { CodeHash = ValueKeccak.Compute(code) },
                callDepth: 0,
                value: 0,
                inputData: default);

            // A frame that silently fell back to the bytecode loop would be timed as if it were the
            // stream, so prove the stream ran before any number is reported. The build is triggered
            // by execution and completes on a background thread, so the first run necessarily misses
            // it; warm up until the counter moves, which also leaves the JIT at tier 1.
            long framesBefore = StreamInterpreter.FramesExecuted;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                Execute();
                if (StreamInterpreter.FramesExecuted != framesBefore) break;
                Thread.Sleep(10);
            }

            if (StreamInterpreter.FramesExecuted == framesBefore)
                throw new InvalidOperationException("the stream interpreter did not run this workload");
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            _environment.Dispose();
            _stateScope.Dispose();
            StreamInterpreter.ForceAllContexts = false;
        }

        [Benchmark]
        public void Execute()
        {
            using VmState<EthereumGasPolicy> evmState = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromULong(10_000_000_000), ExecutionType.TRANSACTION, _environment,
                new StackAccessTracker(), _stateProvider.TakeSnapshot());
            _virtualMachine.ExecuteTransaction<OffFlag>(evmState, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }
    }
}
