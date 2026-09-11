// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Benchmarks.State;

/// <summary>Compares ordinal parent reads with the shared-cache path below, at and above its capacity.</summary>
/// <remarks>
/// Uses production DI and warm in-memory backends. WarmHits excludes setup; MissBlock includes a new plan,
/// worker coverage and parent scope, and reads each slot once. Neither case measures disk I/O or parallel execution.
/// </remarks>
[MemoryDiagnoser]
public class BalOrdinalReadBenchmarks
{
    [Params(64, 256, 4096)]
    public int Reads { get; set; }

    [Params(false, true)]
    public bool Ordinal { get; set; }

    [Params(false, true)]
    public bool Flat { get; set; }

    private IContainer _container = null!;
    private ILifetimeScope _lifetime = null!;
    private IWorldState _parent = null!;
    private BlockAccessListBasedWorldState _state = null!;
    private PreBlockCaches _caches = null!;
    private StorageCell[] _cells = null!;
    private Block _block = null!;
    private BalReadStoragePlan? _warmPlan;
    private BalReadCoverage? _warmCoverage;
    private IDisposable? _warmScope;

    [GlobalSetup(Target = nameof(WarmHits))]
    public void SetupWarm() => Setup(warm: true);

    [GlobalSetup(Target = nameof(MissBlock))]
    public void SetupMiss() => Setup(warm: false);

    private void Setup(bool warm)
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new BlocksConfig(), new FlatDbConfig { Enabled = Flat }))
            .AddSingleton(new PreBlockCachesConfig { StorageCacheSetsBits = 7 })
            .Build();
        _lifetime = _container.BeginLifetimeScope(builder => builder
            .AddSingleton<IWorldStateScopeProvider>(_container.Resolve<IWorldStateManager>().GlobalWorldState)
            .AddModule(new PrewarmerModule.PrewarmerMainProcessingModule(new BlocksConfig()))
            .AddScoped<BlockAccessListBasedWorldState>());
        _parent = _lifetime.Resolve<IWorldState>();
        _state = _lifetime.Resolve<BlockAccessListBasedWorldState>();
        _caches = _lifetime.Resolve<PreBlockCaches>();
        if (_caches.StorageCache.Capacity != 256) throw new InvalidOperationException("Unexpected shared-cache capacity.");
        _cells = new StorageCell[Reads];
        ReadOnlyAccountChanges[] accounts = new ReadOnlyAccountChanges[16];
        Random random = new(42);
        using (_parent.BeginScope(IWorldState.PreGenesis))
        {
            for (int a = 0; a < accounts.Length; a++)
            {
                byte[] addressBytes = new byte[Address.Size];
                addressBytes[^1] = (byte)(a + 1);
                Address address = new(addressBytes);
                _parent.CreateAccount(address, 100);
                UInt256[] slots = new UInt256[Reads / accounts.Length];
                for (int s = 0; s < slots.Length; s++)
                {
                    byte[] key = new byte[32];
                    random.NextBytes(key);
                    slots[s] = new UInt256(key);
                }
                Array.Sort(slots);
                for (int s = 0; s < slots.Length; s++)
                {
                    StorageCell cell = new(address, slots[s]);
                    _cells[s * accounts.Length + a] = cell;
                    if (s % 4 != 0)
                    {
                        byte[] value = new byte[32];
                        Array.Fill(value, (byte)42);
                        _parent.Set(cell, value);
                    }
                }
                accounts[a] = new(address, [], slots, [], [], []);
            }
            _parent.Commit(Amsterdam.Instance, isGenesis: true);
            _parent.CommitTree(0);
            BlockHeader header = Build.A.BlockHeader.WithNumber(0).WithStateRoot(_parent.StateRoot).TestObject;
            _block = Build.A.Block.WithHeader(header)
                .WithBlockAccessList(new ReadOnlyBlockAccessList(accounts, accounts.Length + Reads)).TestObject;
        }
        ulong expected = (ulong)(Reads * 3 / 4) * 42;
        if (MissBlock() != expected) throw new InvalidOperationException("Incorrect miss checksum.");
        if (warm)
        {
            _warmScope = _parent.BeginScope(_block.Header);
            _warmPlan = CreatePlan();
            _warmCoverage = Attach(_warmPlan);
            if (ReadAll() != expected || WarmHits() != expected) throw new InvalidOperationException("Incorrect hit checksum.");
        }
    }

    private BalReadStoragePlan CreatePlan() => new(_block.BlockAccessList!, Ordinal ? _caches.StorageCache.Capacity : int.MaxValue);

    private BalReadCoverage Attach(BalReadStoragePlan plan)
    {
        BalReadCoverage coverage = plan.CreateCoverage();
        _state.Setup(_block, coverage);
        _state.SetParentReader(_parent);
        _state.SetBlockAccessIndex(1);
        return coverage;
    }

    [Benchmark]
    public ulong WarmHits()
    {
        _warmCoverage!.StartSlice();
        return ReadAll();
    }

    [Benchmark]
    public ulong MissBlock()
    {
        using IDisposable scope = _parent.BeginScope(_block.Header);
        using BalReadStoragePlan plan = CreatePlan();
        Attach(plan);
        ulong sum = ReadAll();
        _state.ClearParentReader();
        return sum;
    }

    private ulong ReadAll()
    {
        ulong sum = 0;
        foreach (ref readonly StorageCell cell in _cells.AsSpan())
        {
            ReadOnlySpan<byte> value = _state.Get(cell);
            sum += value.IsEmpty ? 0UL : value[0];
        }
        return sum;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _state?.ClearParentReader();
        _warmScope?.Dispose();
        _warmPlan?.Dispose();
        _lifetime?.Dispose();
        _container?.Dispose();
    }
}
