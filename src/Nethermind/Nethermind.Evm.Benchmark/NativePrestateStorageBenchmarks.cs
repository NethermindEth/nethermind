// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark;

/// <summary>Measures first and repeated prestate slot capture against warm committed state.</summary>
/// <remarks>
/// First access includes one dictionary clear per batch and retains its capacity. Trace construction,
/// dictionary growth, state seeding, and cache warming are outside the measured methods.
/// </remarks>
[MemoryDiagnoser]
public class NativePrestateStorageBenchmarks
{
    private const int SlotCount = 64;

    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private IDisposable _stateScope = null!;
    private StorageTracer _tracer = null!;
    private UInt256[] _slots = null!;
    private Dictionary<UInt256, UInt256> _capturedStorage = null!;

    [Params(false, true)]
    public bool UseFlatDb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = UseFlatDb }))
            .Build();
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        _processingScope = _container.BeginLifetimeScope(builder => builder.AddSingleton(scopeProvider));
        IWorldState state = _processingScope.Resolve<IWorldState>();
        _slots = new UInt256[SlotCount];
        UInt256 storedValue = 7;
        BlockHeader baseBlock;
        using (IDisposable seed = state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(TestItem.AddressA, UInt256.One);
            for (int i = 0; i < SlotCount; i++)
            {
                _slots[i] = (UInt256)(ulong)i;
                state.Set(new StorageCell(TestItem.AddressA, _slots[i]), in storedValue);
            }

            state.Commit(Osaka.Instance);
            state.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(state.StateRoot).TestObject;
        }

        _stateScope = state.BeginScope(baseBlock);
        foreach (UInt256 slot in _slots)
        {
            state.Get(new StorageCell(TestItem.AddressA, slot), out UInt256 value);
            if (value != storedValue)
            {
                throw new InvalidOperationException("The measurement scope must see every committed storage value.");
            }
        }

        _tracer = new StorageTracer(state);
        foreach (UInt256 slot in _slots)
        {
            _tracer.Capture(slot);
        }

        Dictionary<AddressAsKey, NativePrestateTracerAccount> accounts =
            (Dictionary<AddressAsKey, NativePrestateTracerAccount>)_tracer.BuildResult().CustomTracerResult!.Value!;
        _capturedStorage = accounts[TestItem.AddressA].Storage!;
        if (_capturedStorage.Count != SlotCount)
        {
            throw new InvalidOperationException("The tracer must capture every seeded storage slot.");
        }
    }

    [Benchmark(OperationsPerInvoke = SlotCount)]
    public int FirstAccessWarmState()
    {
        _capturedStorage.Clear();
        for (int i = 0; i < _slots.Length; i++)
        {
            _tracer.Capture(_slots[i]);
        }

        return _capturedStorage.Count;
    }

    [Benchmark(OperationsPerInvoke = SlotCount)]
    public int RepeatedAccess()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            _tracer.Capture(_slots[i]);
        }

        return _capturedStorage.Count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _tracer?.Dispose();
        _stateScope?.Dispose();
        _processingScope?.Dispose();
        _container?.Dispose();
    }

    private sealed class StorageTracer(IWorldState state)
        : NativePrestateTracer(state, new GethTraceOptions { Tracer = PrestateTracer },
            Hash256.Zero, TestItem.AddressA, TestItem.AddressA)
    {
        public void Capture(UInt256 slot) => LookupStorage(TestItem.AddressA, slot);
    }
}
