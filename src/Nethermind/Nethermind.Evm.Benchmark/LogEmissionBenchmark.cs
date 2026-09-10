// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
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
    private Transaction _transaction = null!;
    private BlockHeader _header = null!;
    private Snapshot _snapshot;

    [Params(0, 100)]
    public int LogCount { get; set; }

    [Params(false, true)]
    public bool Warmup { get; set; }

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
        byte[] code = new byte[LogCount * 11 + 1];
        for (int i = 0; i < LogCount; i++)
        {
            // Four zero topics, 1 KiB of data at offset 1 KiB.
            new byte[] { 0x5f, 0x5f, 0x5f, 0x5f, 0x61, 0x04, 0x00, 0x61, 0x04, 0x00, 0xa4 }
                .CopyTo(code, i * 11);
        }
        _state.InsertCode(TestItem.AddressB, Keccak.Compute(code), code, Osaka.Instance);
        _state.Commit(Osaka.Instance);
        _snapshot = _state.TakeSnapshot();
        _processor = _scope.Resolve<ITransactionProcessor>();
        _header = Build.A.BlockHeader.WithNumber(1).WithGasLimit(30_000_000).WithBaseFee(0).TestObject;
        _processor.SetBlockExecutionContext(_header);
        _transaction = Build.A.Transaction.WithTo(TestItem.AddressB).WithGasLimit(10_000_000)
            .WithGasPrice(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        if (!Execute().TransactionExecuted) throw new InvalidOperationException("Benchmark transaction did not execute.");
    }

    [Benchmark]
    public TransactionResult Execute()
    {
        _header.GasUsed = 0;
        TransactionResult result = Warmup
            ? _processor.Warmup(_transaction, NullTxTracer.Instance)
            : _processor.BuildUp(_transaction, NullTxTracer.Instance);
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
