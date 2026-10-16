// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Pbt;
using Nethermind.State.Pbt.ScopeProvider;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Compares the EIP-8297 partitioned-binary-tree scope provider (<see cref="PbtScopeProvider"/>)
/// against the plain Merkle-Patricia trie scope provider (<see cref="TrieStoreScopeProvider"/>)
/// at the <see cref="IWorldStateScopeProvider"/> level.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(3)]
[MinIterationCount(3)]
[MaxIterationCount(10)]
[InvocationCount(1)]
public class PbtScopeProviderBenchmark
{
    public enum Backend
    {
        Trie,
        Pbt
    }

    public enum SlotLayout
    {
        // Consecutive storage-zone slots share one PBT storage stem.
        Dense,
        // Slots spaced by 256 maximize PBT storage-stem fan-out.
        Spread
    }

    private IContainer? _container;
    private IPbtDbManager? _pbtManager;
    private IWorldStateScopeProvider _provider = null!;

    private BlockHeader _baseHeader = null!;
    private Address[] _addresses = null!;

    [Params(Backend.Trie, Backend.Pbt)]
    public Backend StateBackend { get; set; }

    [Params(100, 500)]
    public int AccountCount { get; set; }

    // Zero measures account merkelization only; positive values also exercise storage.
    [Params(0, 20)]
    public int StorageSlotsPerAccount { get; set; }

    [Params(SlotLayout.Dense, SlotLayout.Spread)]
    public SlotLayout StorageLayout { get; set; }

    [Params(1, 32, 128)]
    public int ChainDepth { get; set; }

    [Params(1, 16)]
    public int FoldConcurrency { get; set; }

    /// <summary>Starts from persistence instead of retained snapshots; the chain depth then describes setup history, not live layers.</summary>
    [Params(false, true)]
    public bool PersistedRoot { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _provider = StateBackend switch
        {
            Backend.Pbt => CreatePbtProvider(),
            Backend.Trie => new TrieStoreScopeProvider(new TestRawTrieStore(new MemDb()), new MemDb(), UnavailableStateHeaderProvider.Instance, LimboLogs.Instance),
            _ => throw new ArgumentOutOfRangeException(nameof(StateBackend))
        };

        _addresses = new Address[AccountCount];
        for (int i = 0; i < AccountCount; i++)
        {
            _addresses[i] = DeriveAddress(i + 1);
        }

        // Commit layers without finalizing so the measured scope has an in-memory chain of the requested depth.
        Hash256 baseRoot;
        using (IWorldStateScopeProvider.IScope scope = _provider.BeginScope(null, new LocalMetrics()))
        {
            for (int block = 1; block <= ChainDepth; block++)
            {
                if (block == 1) WriteState(scope, nonceOffset: block);
                else
                {
                    using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1);
                    int index = block % AccountCount;
                    batch.Set(_addresses[index], new Account((ulong)(index + block + 1), (UInt256)(ulong)(index + 1)));
                }
                scope.UpdateRootHash();
                scope.Commit((ulong)block);
            }

            baseRoot = scope.RootHash;
        }

        _baseHeader = Build.A.BlockHeader.WithNumber((ulong)ChainDepth).WithStateRoot(baseRoot).TestObject;
        if (PersistedRoot) _pbtManager?.FlushCache(CancellationToken.None);
    }

    private IWorldStateScopeProvider CreatePbtProvider()
    {
        PbtConfig config = new()
        {
            FoldConcurrency = FoldConcurrency,
            CompactSize = 256,
            CompactionOffset = 0,
            MinReorgDepth = 256,
            MaxReorgDepth = 512,
            EnableLongFinality = false,
        };
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(config))
            .AddModule(new PbtModule(config));
        _container = builder.Build();
        _pbtManager = _container.Resolve<IPbtDbManager>();
        return _container.Resolve<PbtWorldStateManager>().GlobalWorldState;
    }

    [Benchmark]
    public Hash256 WriteAndUpdateRootHash()
    {
        using IWorldStateScopeProvider.IScope scope = _provider.BeginScope(_baseHeader, new LocalMetrics());
        WriteState(scope, mixed: true);
        scope.UpdateRootHash();
        return scope.RootHash;
    }

    [Benchmark]
    public Account? ReadAccounts()
    {
        using IWorldStateScopeProvider.IScope scope = _provider.BeginScope(_baseHeader, new LocalMetrics());
        Account? last = null;
        for (int i = 0; i < AccountCount; i++)
        {
            last = scope.Get(_addresses[i]);
        }

        return last;
    }

    private void WriteState(IWorldStateScopeProvider.IScope scope, int nonceOffset = 0, bool mixed = false)
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(AccountCount);
        for (int i = 0; i < AccountCount; i++)
        {
            bool delete = mixed && i % 10 == 0;
            Address address = mixed && i % 10 is 1 or 2 ? DeriveAddress(AccountCount + i + 1) : _addresses[i];
            batch.Set(address, delete ? null : new Account((ulong)(i + 1 + nonceOffset), (UInt256)(ulong)(i + 1)));

            if (!delete && StorageSlotsPerAccount > 0)
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch =
                    batch.CreateStorageWriteBatch(address, estimatedEntries: StorageSlotsPerAccount);
                for (int s = 0; s < StorageSlotsPerAccount; s++)
                {
                    uint value = mixed && s % 10 == 0 ? 0 : (uint)((s + (mixed ? 2 : 1)) & 0xFF);
                    storageBatch.Set(SlotKey(s), (UInt256)value);
                }
            }
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _container?.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private const ulong StorageZoneBase = 64;

    private UInt256 SlotKey(int s) => StorageLayout switch
    {
        SlotLayout.Dense => (UInt256)(StorageZoneBase + (ulong)s),
        SlotLayout.Spread => (UInt256)(StorageZoneBase + (ulong)s * 256),
        _ => throw new ArgumentOutOfRangeException(nameof(StorageLayout))
    };

    private static Address DeriveAddress(int index) =>
        new(Keccak.Compute(Address.FromNumber((UInt256)(ulong)index).Bytes));
}
