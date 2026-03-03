// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Blockchain;

namespace Nethermind.Evm.Benchmark
{
    [MemoryDiagnoser]
    public class EvmBenchmarks
    {
        public static byte[] ByteCode { get; set; }

        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private IVirtualMachine _virtualMachine;
        private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();
        private CallFrame<EthereumGasPolicy> _callFrame;
        private AccessTrackingState _trackingState;
        private IWorldState _stateProvider;

        [GlobalSetup]
        public void GlobalSetup()
        {
            ByteCode = Bytes.FromHexString(Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE") ?? string.Empty);
            Console.WriteLine($"Running benchmark for bytecode {ByteCode?.ToHexString()}");

            _stateProvider = TestWorldStateFactory.CreateForTest();
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            _stateProvider.Commit(_spec);
            EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
            _virtualMachine = new EthereumVirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, LimboLogs.Instance);
            _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(_header, _spec));
            _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

            _trackingState = AccessTrackingState.RentState();
            _callFrame = CallFrame<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromLong(long.MaxValue),
                ExecutionType.TRANSACTION,
                new CodeInfo(ByteCode), Address.Zero, Address.Zero, Address.Zero,
                default, default,
                default, _trackingState, _stateProvider.TakeSnapshot());
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            _callFrame.Dispose();
            AccessTrackingState.ResetAndReturn(_trackingState);
        }

        [Benchmark]
        public void ExecuteCode()
        {
            _virtualMachine.ExecuteTransaction<OffFlag>(_callFrame, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }
    }
}
