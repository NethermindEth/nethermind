// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Nested CALL chains of a given depth, repeated from the top-level frame, to measure per-frame setup and teardown
/// at depths inside and beyond the per-VM frame cache.
/// </summary>
[MemoryDiagnoser]
public class CallChainBenchmarks
{
    private const int WorkloadWarmupTransactions = 2_000;
    private const ulong GasLimit = 30_000_000;
    private const int Iterations = 100;
    private const int LoopStart = 3;

    private static readonly Address Recursive = Address.FromNumber(0x1000);

    // d = CALLDATALOAD(0); if d == 0 stop; else CALL(self, input = d - 1).
    private static readonly byte[] RecursiveCode = Prepare.EvmCode
        .PushData(Push2(0))
        .Op(Instruction.CALLDATALOAD)
        .Op(Instruction.DUP1)
        .PushData(Push2(10))
        .Op(Instruction.JUMPI)
        .Op(Instruction.STOP)
        .Op(Instruction.JUMPDEST)
        .PushData(1)
        .Op(Instruction.SWAP1)
        .Op(Instruction.SUB)
        .PushData(0)
        .Op(Instruction.MSTORE)
        .PushData(0)
        .PushData(0)
        .PushData(32)
        .PushData(0)
        .PushData(0)
        .Op(Instruction.ADDRESS)
        .Op(Instruction.GAS)
        .Op(Instruction.CALL)
        .Op(Instruction.POP)
        .Op(Instruction.STOP)
        .Done;

    private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private IDisposable _stateScope = null!;
    private IWorldState _state = null!;
    private IVirtualMachine _vm = null!;
    private CodeInfo _driver = null!;

    /// <summary>Frames opened below the top level per chain; the deepest frame runs at this call depth.</summary>
    [Params(7, 16, 32)]
    public int Depth { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        IReleaseSpec spec = Cancun.Instance;
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(spec))
            .Build();
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        _processingScope = _container.BeginLifetimeScope(builder => builder.AddSingleton(scopeProvider));
        _state = _processingScope.Resolve<IWorldState>();
        _vm = _processingScope.Resolve<IVirtualMachine>();
        _stateScope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(Address.Zero, 1000.Ether);
        _state.CreateAccount(Recursive, UInt256.Zero);
        _state.InsertCode(Recursive, RecursiveCode, spec);
        _state.Commit(spec);
        _vm.SetBlockExecutionContext(new BlockExecutionContext(_header, spec));
        _vm.SetTxExecutionContext(new TxExecutionContext(Address.Zero, _processingScope.Resolve<ICodeInfoRepository>(), null, 0));

        // Each iteration: MSTORE(0, Depth - 1); CALL(Recursive, input = 32 bytes at 0); POP.
        _driver = new CodeInfo(Prepare.EvmCode
            .PushData(Push2(Iterations))
            .Op(Instruction.JUMPDEST)
            .PushData(Depth - 1)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(0)
            .PushData(0)
            .PushData(32)
            .PushData(0)
            .PushData(0)
            .PushData(Recursive)
            .Op(Instruction.GAS)
            .Op(Instruction.CALL)
            .Op(Instruction.POP)
            .PushData(1)
            .Op(Instruction.SWAP1)
            .Op(Instruction.SUB)
            .Op(Instruction.DUP1)
            .PushData(Push2(LoopStart))
            .Op(Instruction.JUMPI)
            .Op(Instruction.STOP)
            .Done);

        // Gas per chain grows linearly with depth only if every chain reaches the deepest frame.
        Console.WriteLine($"// CallChain depth {Depth}: gas used {GasLimit - Execute()}");
        for (int i = 1; i < WorkloadWarmupTransactions; i++)
        {
            Execute();
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _stateScope?.Dispose();
        _processingScope?.Dispose();
        _container?.Dispose();
    }

    [Benchmark]
    public ulong CallChain() => Execute();

    private static byte[] Push2(int value) => [(byte)(value >> 8), (byte)value];

    /// <returns>The gas left in the top-level frame.</returns>
    private ulong Execute()
    {
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Address.Zero,
            codeSource: Address.Zero,
            caller: Address.Zero,
            codeInfo: _driver,
            callDepth: 0,
            value: 0,
            inputData: default);

        ulong remaining;
        using (VmState<EthereumGasPolicy> vmState = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(GasLimit),
            ExecutionType.TRANSACTION,
            environment,
            new StackAccessTracker(),
            _state.TakeSnapshot()))
        {
            TransactionSubstate substate = _vm.ExecuteTransaction<OffFlag>(vmState, _state, NullTxTracer.Instance);
            if (substate.EvmExceptionType != EvmExceptionType.None)
                throw new InvalidOperationException($"Benchmark bytecode halted with {substate.EvmExceptionType}");
            remaining = EthereumGasPolicy.GetRemainingGas(in vmState.Gas);
        }

        _state.Reset();
        return remaining;
    }
}
