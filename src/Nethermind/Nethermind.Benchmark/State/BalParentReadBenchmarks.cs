// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Benchmarks.State;

/// <summary>Measures immutable BAL parent reads, including scope reset and retained-cache reuse.</summary>
/// <remarks>
/// Uses production DI with in-memory databases and no shared prewarming cache. Each invocation
/// opens a parent scope and reads 512 full-width slots across 16 accounts, or their account fields.
/// The backend is warm in memory; this does not measure disk I/O, EVM execution or worker scheduling.
/// </remarks>
[MemoryDiagnoser]
public class BalParentReadBenchmarks
{
    public enum ReadPattern { First, Consecutive, Alternating, Accounts }

    [ParamsAllValues]
    public ReadPattern Pattern { get; set; }

    private IContainer _container = null!;
    private IWorldState _parent = null!;
    private BlockAccessListBasedWorldState _state = null!;
    private BalReadStoragePlan _plan = null!;
    private BalReadCoverage _coverage = null!;
    private Block _block = null!;
    private StorageCell[] _cells = null!;

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new BlocksConfig { PreWarming = PreWarmMode.None }))
            .Map<IWorldStateScopeProvider, IWorldStateManager>(manager => manager.GlobalWorldState)
            .AddScoped<BlockAccessListBasedWorldState>()
            .Build();
        _parent = _container.Resolve<IWorldState>();
        _state = _container.Resolve<BlockAccessListBasedWorldState>();
        Random random = new(42);
        _cells = new StorageCell[512];
        ReadOnlyAccountChanges[] accounts = new ReadOnlyAccountChanges[16];
        using (_parent.BeginScope(IWorldState.PreGenesis))
        {
            for (int a = 0; a < accounts.Length; a++)
            {
                byte[] addressBytes = new byte[Address.Size];
                addressBytes[^1] = (byte)(a + 1);
                Address address = new(addressBytes);
                _parent.CreateAccount(address, 100, 1);
                UInt256[] slots = new UInt256[_cells.Length / accounts.Length];
                for (int s = 0; s < slots.Length; s++)
                {
                    byte[] slotBytes = new byte[32];
                    random.NextBytes(slotBytes);
                    slots[s] = new UInt256(slotBytes, isBigEndian: false);
                    StorageCell cell = new(address, slots[s]);
                    // Include missing slots and equal-but-distinct address instances.
                    if (s % 4 != 0) _parent.Set(cell, [42]);
                    _cells[s * accounts.Length + a] = new(new Address(addressBytes), slots[s]);
                }
                Array.Sort(slots);
                accounts[a] = new(address, [], slots, [], [], []);
            }
            _parent.Commit(Amsterdam.Instance, isGenesis: true);
            _parent.CommitTree(0);
            BlockHeader header = Build.A.BlockHeader.WithStateRoot(_parent.StateRoot).WithNumber(0).TestObject;
            _block = Build.A.Block.WithHeader(header)
                .WithBlockAccessList(new ReadOnlyBlockAccessList(accounts, accounts.Length + _cells.Length)).TestObject;
        }
        _plan = new(_block.BlockAccessList!);
        _coverage = _plan.CreateCoverage();
        ulong expected = Pattern switch
        {
            ReadPattern.First => 384 * 42UL,
            ReadPattern.Accounts => 512 * 4 * 101UL,
            _ => 384 * 42UL * 8
        };
        if (ReadBlock() != expected) throw new InvalidOperationException("Unexpected parent read values.");
    }

    [Benchmark]
    public ulong ReadBlock()
    {
        using IDisposable scope = _parent.BeginScope(_block.Header);
        _state.Setup(_block, _coverage);
        _state.SetParentReader(_parent);
        _coverage.StartSlice();
        ulong sum = 0;
        int passes = Pattern is ReadPattern.Alternating or ReadPattern.Accounts ? 4 : 1;
        for (int pass = 0; pass < passes; pass++)
        {
            _state.SetBlockAccessIndex((uint)(pass + 1));
            _coverage.StartSlice();
            foreach (ref readonly StorageCell cell in _cells.AsSpan())
            {
                if (Pattern == ReadPattern.Accounts)
                {
                    sum += (ulong)_state.GetBalance(cell.Address) + _state.GetNonce(cell.Address);
                    continue;
                }
                int repeats = Pattern == ReadPattern.Consecutive ? 4 : 1;
                for (int repeat = 0; repeat < repeats; repeat++)
                {
                    ReadOnlySpan<byte> value = _state.Get(cell);
                    sum += value.IsEmpty ? 0UL : value[0];
                    if (Pattern != ReadPattern.First)
                    {
                        value = _state.GetOriginal(cell);
                        sum += value.IsEmpty ? 0UL : value[0];
                    }
                }
            }
        }
        _state.ClearParentReader();
        return sum;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _plan?.Dispose();
        _container?.Dispose();
    }
}
