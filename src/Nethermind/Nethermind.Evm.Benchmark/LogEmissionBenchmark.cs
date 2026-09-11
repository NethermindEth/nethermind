// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark;

[MemoryDiagnoser]
public class LogEmissionBenchmark
{
    private IContainer _container = null!;
    private ILifetimeScope _scope = null!;
    private IDisposable _stateScope = null!;
    private IWorldState _state = null!;
    private ITransactionProcessor _processor = null!;
    private IBlockchainBridge _bridge = null!;
    private Transaction _transaction = null!;
    private BlockHeader _header = null!;
    private Snapshot _snapshot;

    [Params(0, 100)]
    public int LogCount { get; set; }

    private static readonly GethTraceOptions WithLogs = GethTraceOptions.Default with
    {
        TracerConfig = JsonSerializer.SerializeToElement(new { withLog = true })
    };

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder().AddModule(new TestNethermindModule(Osaka.Instance)).Build();
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        _scope = _container.BeginLifetimeScope(builder =>
            builder.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned());
        _state = _scope.Resolve<IWorldState>();
        _stateScope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(TestItem.AddressA, 1.Ether);
        _state.CreateAccount(TestItem.AddressB, 0);
        byte[] code = new byte[LogCount * 11 + 4];
        for (int i = 0; i < LogCount; i++)
        {
            // Four zero topics, 1 KiB of data at offset 1 KiB.
            new byte[] { 0x5f, 0x5f, 0x5f, 0x5f, 0x61, 0x04, 0x00, 0x61, 0x04, 0x00, 0xa4 }
                .CopyTo(code, i * 11);
        }
        // Read one slot so access-list generation exercises convergence.
        new byte[] { 0x5f, 0x54, 0x50 }.CopyTo(code, LogCount * 11);
        _state.InsertCode(TestItem.AddressB, Keccak.Compute(code), code, Osaka.Instance);
        _state.Commit(Osaka.Instance);
        _state.CommitTree(1);
        _snapshot = _state.TakeSnapshot();
        _processor = _scope.Resolve<ITransactionProcessor>();
        _bridge = _scope.Resolve<IBlockchainBridge>();
        _header = Build.A.BlockHeader.WithNumber(1).WithGasLimit(30_000_000).WithBaseFee(0)
            .WithStateRoot(_state.StateRoot).TestObject;
        _processor.SetBlockExecutionContext(_header);
        _transaction = Build.A.Transaction.WithTo(TestItem.AddressB).WithGasLimit(10_000_000)
            .WithGasPrice(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        if (!Warmup().TransactionExecuted || Call().Error is not null || EstimateGas().Error is not null
            || CreateAccessList().Error is not null)
            throw new InvalidOperationException("Benchmark transaction did not execute.");
    }

    [Benchmark]
    public CallOutput Call() => _bridge.Call(_header, _transaction);

    [Benchmark]
    public CallOutput EstimateGas() => _bridge.EstimateGas(_header, _transaction, 0);

    [Benchmark]
    public CallOutput CreateAccessList() => _bridge.CreateAccessList(_header, _transaction, null, false);

    [Benchmark]
    public void CallTrace() => Trace(GethTraceOptions.Default);

    [Benchmark]
    public void CallTraceWithLogs() => Trace(WithLogs);

    private void Trace(GethTraceOptions options)
    {
        _header.GasUsed = 0;
        using NativeCallTracer tracer = new(_transaction, Osaka.Instance, options);
        _processor.CallAndRestore(_transaction, tracer);
        using GethLikeTxTrace result = tracer.BuildResult();
    }

    [Benchmark]
    public TransactionResult Warmup()
    {
        _header.GasUsed = 0;
        TransactionResult result = _processor.Warmup(_transaction, NullTxTracer.Instance);
        _state.Restore(_snapshot);
        return result;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stateScope.Dispose();
        _scope.Dispose();
        _container.Dispose();
    }
}
