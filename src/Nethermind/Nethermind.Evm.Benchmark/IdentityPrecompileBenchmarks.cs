// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark;

/// <summary>Measures the inline STATICCALL path to the ID precompile at the input sizes that drive its cost.</summary>
/// <remarks>
/// Results are normalized per ID call. The chains in <see cref="OpcodeChainBenchmarks"/> call ID with no input,
/// which measures the call machinery alone; here the output is what is being copied and reported.
/// <see cref="InputSize"/> at or below the VM's retained-scratch limit reaches steady state after the first call,
/// so the allocation column reads per-call reuse. The size above the limit is served by a fresh buffer on every
/// call and so belongs to a row of its own rather than to that trend.
/// </remarks>
[MemoryDiagnoser]
public class IdentityPrecompileBenchmarks
{
    private const int CallsPerInvoke = 32;
    private const ulong GasLimit = 30_000_000;
    private const ulong GasPerCall = 1_000_000;

    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private IDisposable _stateScope = null!;
    private IWorldState _state = null!;
    private IVirtualMachine _vm = null!;
    private CodeInfo _code = null!;

    [Params(32, 1024, 64 * 1024, 96 * 1024)]
    public int InputSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .Build();
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        _processingScope = _container.BeginLifetimeScope(builder => builder.AddSingleton(scopeProvider));
        _state = _processingScope.Resolve<IWorldState>();
        _vm = _processingScope.Resolve<IVirtualMachine>();
        _stateScope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(Address.Zero, UInt256.One);
        _state.Commit(Osaka.Instance);
        _vm.SetBlockExecutionContext(new BlockExecutionContext(Build.A.BlockHeader.TestObject, Osaka.Instance));
        _vm.SetTxExecutionContext(new TxExecutionContext(Address.Zero, _processingScope.Resolve<ICodeInfoRepository>(), null, 0));
        _code = new CodeInfo(BuildCode());

        for (int i = 0; i < 1_000; i++) ExecuteContract();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stateScope?.Dispose();
        _processingScope?.Dispose();
        _container?.Dispose();
    }

    [Benchmark(OperationsPerInvoke = CallsPerInvoke)]
    public void StaticCallIdentity() => ExecuteContract();

    private void ExecuteContract()
    {
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Address.Zero, codeSource: Address.Zero, caller: Address.Zero,
            codeInfo: _code, callDepth: 0, value: 0, inputData: default);
        using StackAccessTracker accessTracker = new();
        using VmState<EthereumGasPolicy> state = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(GasLimit), ExecutionType.TRANSACTION, environment,
            accessTracker, _state.TakeSnapshot());
        TransactionSubstate result = _vm.ExecuteTransaction<OffFlag>(state, _state, NullTxTracer.Instance);
        if (result.IsError || result.ShouldRevert)
            throw new InvalidOperationException($"Identity chain execution failed: {result.EvmExceptionType}");
        _state.Reset();
    }

    /// <summary>Calls ID <see cref="CallsPerInvoke"/> times, each copying its output back into memory.</summary>
    /// <remarks>The input is left zero: only its length is measured here, never its contents.</remarks>
    private byte[] BuildCode()
    {
        UInt256 size = (UInt256)InputSize;
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < CallsPerInvoke; i++)
        {
            code = code
                .STATICCALL(GasPerCall, IdentityPrecompile.Address, 0, size, size, size)
                .Op(Instruction.POP);
        }

        return code.Op(Instruction.STOP).Done;
    }
}
