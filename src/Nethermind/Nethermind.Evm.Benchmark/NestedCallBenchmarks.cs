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
    /// Measures the full inner-call frame cycle (frame rent, child memory, return) the way a
    /// Multicall-style aggregator exercises it: the caller STATICCALLs a callee that writes
    /// memory and returns a word, 64 times, straight-line.
    /// </summary>
    [MemoryDiagnoser]
    public class NestedCallBenchmarks
    {
        private const int CallCount = 64;

        private static readonly Address _calleeAddress = new("0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.MuirGlacierBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IVirtualMachine _virtualMachine;
        private IWorldState _stateProvider;
        private IDisposable _stateScope = null!;
        private CodeInfo _callerCodeInfo;

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

            _callerCodeInfo = new CodeInfo(BuildCallerCode());
        }

        [GlobalCleanup]
        public void GlobalCleanup() => _stateScope.Dispose();

        [Benchmark]
        public void ExecuteNestedCalls()
        {
            using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
                executingAccount: Address.Zero,
                codeSource: Address.Zero,
                caller: Address.Zero,
                codeInfo: _callerCodeInfo,
                callDepth: 0,
                value: 0,
                inputData: default);

            using (VmState<EthereumGasPolicy> vmState = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromLong(10_000_000L),
                ExecutionType.TRANSACTION,
                _virtualMachine.MemoryArena,
                environment,
                new StackAccessTracker(),
                _stateProvider.TakeSnapshot()))
            {
                _virtualMachine.ExecuteTransaction<OffFlag>(vmState, _stateProvider, NullTxTracer.Instance);
            }

            _stateProvider.Reset();
        }

        // The callee writes a word to memory and returns it: enough to force child frame
        // memory backing on every invocation.
        private static byte[] BuildCalleeCode() => Prepare.EvmCode
            .PushData(42)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        private static byte[] BuildCallerCode()
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
