// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Db;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.Evm.Benchmark
{
    // Decomposes the per-sub-call allocation that dominates real heavy eth_calls. Three loops,
    // identical except for the body, run against an in-memory MemDb world state (no node/DB):
    //   NoCall      - loop only (baseline; outer-frame alloc, amortized)
    //   CallStop    - STATICCALL a contract whose body is just STOP (isolates CALL-frame setup)
    //   CallSload   - STATICCALL a contract that does one SLOAD (CALL frame + a storage read)
    // Comparing the MemoryDiagnoser Allocated column localizes the ~476 B/sub-call:
    //   CallStop - NoCall   = per-CALL-frame allocation
    //   CallSload - CallStop = per-SLOAD allocation
    [MemoryDiagnoser]
    public class ContractCallLoopBenchmark
    {
        private static readonly Address SloadTarget = new("0x0000000000000000000000000000000000000100");
        private static readonly Address StopTarget = new("0x0000000000000000000000000000000000000200");

        private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.MuirGlacierBlockNumber);
        private readonly ITxTracer _txTracer = NullTxTracer.Instance;
        private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.MuirGlacierBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private readonly IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();

        private IVirtualMachine _virtualMachine;
        private CodeInfo _callerSload;
        private CodeInfo _callerStop;
        private CodeInfo _callerNoCall;
        private IWorldState _stateProvider;
        private IDisposable _scope;

        [GlobalSetup]
        public void GlobalSetup()
        {
            byte[] sloadTargetCode = Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).Op(Instruction.POP).Op(Instruction.STOP).Done;
            byte[] stopTargetCode = Prepare.EvmCode.Op(Instruction.STOP).Done;

            _callerSload = new CodeInfo(Prepare.EvmCode
                .Op(Instruction.JUMPDEST).StaticCall(SloadTarget, 100000).Op(Instruction.POP).PushData(0).Op(Instruction.JUMP).Done);
            _callerStop = new CodeInfo(Prepare.EvmCode
                .Op(Instruction.JUMPDEST).StaticCall(StopTarget, 100000).Op(Instruction.POP).PushData(0).Op(Instruction.JUMP).Done);
            // Baseline: same loop shape, no CALL — push a value and pop it, burning gas via GAS.
            _callerNoCall = new CodeInfo(Prepare.EvmCode
                .Op(Instruction.JUMPDEST).Op(Instruction.GAS).Op(Instruction.POP).PushData(0).Op(Instruction.JUMP).Done);

            IDbProvider dbProvider = TestMemDbProvider.Init();
            _stateProvider = TestWorldStateFactory.CreateForTest(dbProvider, LimboLogs.Instance);
            _scope = _stateProvider.BeginScope(IWorldState.PreGenesis);
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether);
            _stateProvider.CreateAccount(SloadTarget, UInt256.Zero);
            _stateProvider.CreateAccount(StopTarget, UInt256.Zero);
            _stateProvider.InsertCode(SloadTarget, sloadTargetCode, _spec);
            _stateProvider.InsertCode(StopTarget, stopTargetCode, _spec);
            _stateProvider.Set(new StorageCell(SloadTarget, 0), [1]);
            _stateProvider.Commit(_spec);
            _stateProvider.CommitTree(0);

            EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
            _virtualMachine = new EthereumVirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, LimboLogs.Instance);
            _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(_header, _spec));
            _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));
        }

        [GlobalCleanup]
        public void GlobalCleanup() => _scope.Dispose();

        // Rent a fresh frame each invocation: ExecuteTransaction consumes the VmState (runs the
        // loop to gas exhaustion), so reusing one would make every call after the first a no-op.
        private void Run(CodeInfo caller)
        {
            ExecutionEnvironment environment = ExecutionEnvironment.Rent(
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: caller,
                callDepth: 0,
                value: 0,
                inputData: default
            );

            VmState<EthereumGasPolicy> evmState = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromLong(100_000_000L), ExecutionType.TRANSACTION, environment, new StackAccessTracker(), _stateProvider.TakeSnapshot());

            _virtualMachine.ExecuteTransaction<OffFlag>(evmState, _stateProvider, _txTracer);

            evmState.Dispose();
            environment.Dispose();
            _stateProvider.Reset();
        }

        [Benchmark(Baseline = true)]
        public void NoCall() => Run(_callerNoCall);

        [Benchmark]
        public void CallStop() => Run(_callerStop);

        [Benchmark]
        public void CallSload() => Run(_callerSload);
    }
}
