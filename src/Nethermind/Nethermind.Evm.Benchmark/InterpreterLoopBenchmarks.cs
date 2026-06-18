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

        private static readonly Address _calleeAddress = new("0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.OsakaActivation);
        private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IVirtualMachine _virtualMachine = null!;
        private IWorldState _stateProvider = null!;
        private IDisposable _stateScope = null!;
        private CodeInfo _computeLoopCode = null!;
        private CodeInfo _nestedCallsCode = null!;

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

            _computeLoopCode = new CodeInfo(BuildComputeLoopCode());
            _nestedCallsCode = new CodeInfo(BuildNestedCallsCode());
        }

        [GlobalCleanup]
        public void GlobalCleanup() => _stateScope.Dispose();

        /// <summary>Arithmetic/jump loop: measures pure dispatch and per-op bookkeeping.</summary>
        [Benchmark]
        public void ComputeLoop() => Execute(_computeLoopCode, gasLimit: 10_000_000L);

        /// <summary>Straight-line STATICCALLs to a returning callee: measures the frame cycle.</summary>
        [Benchmark]
        public void NestedCalls() => Execute(_nestedCallsCode, gasLimit: 10_000_000L);

        private void Execute(CodeInfo codeInfo, long gasLimit)
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
                EthereumGasPolicy.FromLong(gasLimit),
                ExecutionType.TRANSACTION,
                environment,
                new StackAccessTracker(),
                _stateProvider.TakeSnapshot()))
            {
                _virtualMachine.ExecuteTransaction<OffFlag>(vmState, _stateProvider, NullTxTracer.Instance);
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
