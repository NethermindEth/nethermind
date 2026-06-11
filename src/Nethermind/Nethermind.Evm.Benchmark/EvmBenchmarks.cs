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
        public static byte[] ByteCode { get; set; } = null!;

        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment = null!;
        private IVirtualMachine _virtualMachine = null!;
        private BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();
        private VmState<EthereumGasPolicy> _evmState = null!;
        private IWorldState _stateProvider = null!;

        [GlobalSetup]
        public void GlobalSetup()
        {
            ByteCode = Bytes.FromHexString(Environment.GetEnvironmentVariable("NETH.BENCHMARK.BYTECODE") ?? string.Empty);
            Console.WriteLine($"Running benchmark for bytecode {ByteCode?.ToHexString()}");

            _stateProvider = TestWorldStateFactory.CreateForTest();
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
                codeInfo: new CodeInfo(ByteCode),
                callDepth: 0,
                value: 0,
                inputData: default
            );

            _evmState = VmState<EthereumGasPolicy>.RentTopLevel(EthereumGasPolicy.FromULong(ulong.MaxValue), ExecutionType.TRANSACTION, _environment, new StackAccessTracker(), _stateProvider.TakeSnapshot());
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            _evmState.Dispose();
            _environment.Dispose();
        }

        [Benchmark]
        public void ExecuteCode()
        {
            _virtualMachine.ExecuteTransaction<OffFlag>(_evmState, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }
    }
}
