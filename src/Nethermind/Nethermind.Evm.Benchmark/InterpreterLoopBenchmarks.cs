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
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark
{
    /// <summary>
    /// Drives the production interpreter loop hard enough for tiered compilation to reach Tier1,
    /// on two workload shapes: a dispatch-bound compute loop and frame-cycle-bound nested calls.
    /// </summary>
    [MemoryDiagnoser]
    public class InterpreterLoopBenchmarks
    {
        private const int CallCount = 64;
        private const int LoopIterations = 1_000;
        private const int WorkloadWarmupTransactions = 100_000;
        private const int OpcodeRefreshTransactions = 500_000;
        private const string CancelableEnvironmentVariable = "NETHERMIND_EVM_BENCHMARK_CANCELABLE";

        private static readonly Address _calleeAddress = new("0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.OsakaActivation);
        private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IVirtualMachine _virtualMachine = null!;
        private IWorldState _stateProvider = null!;
        private IDisposable _stateScope = null!;
        private CodeInfo _computeLoopCode = null!;
        private CodeInfo _neverTakenJumpIfLoopCode = null!;
        private CodeInfo _alternatingJumpIfLoopCode = null!;
        private CodeInfo _nestedCallsCode = null!;
        private CodeInfo _stopCode = null!;
        private ITxTracer _tracer = null!;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _stateProvider = TestWorldStateFactory.CreateForTest();
            _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether);
            _stateProvider.CreateAccount(_calleeAddress, 1.Ether);
            _stateProvider.InsertCode(_calleeAddress, BuildCalleeCode(), _spec);
            _stateProvider.Commit(_spec);

            EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
            _virtualMachine = new EthereumVirtualMachine(new TestBlockhashProvider(), MainnetSpecProvider.Instance, new OneLoggerLogManager(NullLogger.Instance));
            _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(_header, _spec));
            _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));
            _tracer = Environment.GetEnvironmentVariable(CancelableEnvironmentVariable) == "1"
                ? new CancellationTxTracer(NullTxTracer.Instance)
                : NullTxTracer.Instance;

            _computeLoopCode = new CodeInfo(BuildComputeLoopCode());
            _neverTakenJumpIfLoopCode = new CodeInfo(BuildNeverTakenJumpIfLoopCode());
            _alternatingJumpIfLoopCode = new CodeInfo(BuildAlternatingJumpIfLoopCode());
            _nestedCallsCode = new CodeInfo(BuildNestedCallsCode());
            _stopCode = new CodeInfo(new byte[] { (byte)Instruction.STOP });

            // Exercise every measured shape until hot, then advance the periodic opcode-table refreshes with STOP
            // so no benchmark tiers its own handlers or frame paths during measurement.
            for (int i = 0; i < WorkloadWarmupTransactions; i++)
            {
                CodeInfo codeInfo = (i & 3) switch
                {
                    0 => _computeLoopCode,
                    1 => _neverTakenJumpIfLoopCode,
                    2 => _alternatingJumpIfLoopCode,
                    _ => _nestedCallsCode,
                };
                Execute(codeInfo, gasLimit: 10_000_000);
            }

            for (int i = WorkloadWarmupTransactions; i < OpcodeRefreshTransactions; i++)
            {
                Execute(_stopCode, gasLimit: 10_000_000);
            }
        }

        [GlobalCleanup]
        public void GlobalCleanup() => _stateScope.Dispose();

        /// <summary>Arithmetic/jump loop: measures pure dispatch and per-op bookkeeping.</summary>
        [Benchmark]
        public void ComputeLoop() => Execute(_computeLoopCode, gasLimit: 10_000_000);

        [Benchmark]
        public void NeverTakenJumpIfLoop() => Execute(_neverTakenJumpIfLoopCode, gasLimit: 10_000_000);

        [Benchmark]
        public void AlternatingJumpIfLoop() => Execute(_alternatingJumpIfLoopCode, gasLimit: 10_000_000);

        /// <summary>Straight-line STATICCALLs to a returning callee: measures the frame cycle.</summary>
        [Benchmark]
        public void NestedCalls() => Execute(_nestedCallsCode, gasLimit: 10_000_000);

        private void Execute(CodeInfo codeInfo, ulong gasLimit)
        {
            using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: codeInfo,
                callDepth: 0,
                value: 0,
                inputData: default);

            using (VmState<EthereumGasPolicy> vmState = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromULong(gasLimit),
                ExecutionType.TRANSACTION,
                environment,
                new StackAccessTracker(),
                _stateProvider.TakeSnapshot()))
            {
                _virtualMachine.ExecuteTransaction<OffFlag>(vmState, _stateProvider, _tracer);
            }

            _stateProvider.Reset();
        }

        private static byte[] BuildComputeLoopCode()
        {
            Prepare code = Prepare.EvmCode
                .PushData(LoopIterations)
                .Op(Instruction.JUMPDEST)      // pc 3 (PUSH2 imm is 2 bytes)
                .PushData(1)
                .Op(Instruction.SWAP1)
                .Op(Instruction.SUB)
                .Op(Instruction.DUP1)
                .PushData(7)
                .Op(Instruction.ADD)
                .PushData(3)
                .Op(Instruction.MUL)
                .Op(Instruction.POP)
                .Op(Instruction.DUP1)
                .PushData(3)                   // JUMPDEST pc
                .Op(Instruction.JUMPI)
                .Op(Instruction.STOP);
            return code.Done;
        }

        private static byte[] BuildNeverTakenJumpIfLoopCode() => Prepare.EvmCode
            .PushData(LoopIterations)
            .Op(Instruction.JUMPDEST)          // pc 3
            .PushData(1)
            .Op(Instruction.SWAP1)
            .Op(Instruction.SUB)
            .Op(Instruction.PUSH0)
            .PushData(3)
            .Op(Instruction.JUMPI)
            .Op(Instruction.DUP1)
            .Op(Instruction.ISZERO)
            .PushData(20)                      // exit JUMPDEST
            .Op(Instruction.JUMPI)
            .PushData(3)
            .Op(Instruction.JUMP)
            .Op(Instruction.JUMPDEST)          // pc 20
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        private static byte[] BuildAlternatingJumpIfLoopCode() => Prepare.EvmCode
            .PushData(LoopIterations)
            .Op(Instruction.JUMPDEST)          // pc 3
            .Op(Instruction.DUP1)
            .PushData(1)
            .Op(Instruction.AND)
            .PushData(14)                      // taken JUMPDEST
            .Op(Instruction.JUMPI)
            .PushData(15)                      // join JUMPDEST
            .Op(Instruction.JUMP)
            .Op(Instruction.JUMPDEST)          // pc 14
            .Op(Instruction.JUMPDEST)          // pc 15
            .PushData(1)
            .Op(Instruction.SWAP1)
            .Op(Instruction.SUB)
            .Op(Instruction.DUP1)
            .Op(Instruction.ISZERO)
            .PushData(28)                      // exit JUMPDEST
            .Op(Instruction.JUMPI)
            .PushData(3)
            .Op(Instruction.JUMP)
            .Op(Instruction.JUMPDEST)          // pc 28
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        private static byte[] BuildCalleeCode() => Prepare.EvmCode
            .PushData(42)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        private static byte[] BuildNestedCallsCode()
        {
            Prepare code = Prepare.EvmCode
                .PushData(42)
                .PushData(0)
                .Op(Instruction.MSTORE);
            for (int i = 0; i < CallCount; i++)
            {
                code = code.StaticCall(_calleeAddress, 50_000).Op(Instruction.POP);
            }

            return code.Op(Instruction.STOP).Done;
        }
    }
}
