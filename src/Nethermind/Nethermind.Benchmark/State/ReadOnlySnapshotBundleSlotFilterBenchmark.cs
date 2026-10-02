// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;
using static Nethermind.Benchmarks.State.FlatWorldStateBenchmarkHarness;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Slot reads of a <see cref="ReadOnlySnapshotBundle"/> over mainnet-sized in-memory layers, through the plain loop
/// (<see cref="Filtered"/> false) and through the negative slot filter (<see cref="ReadOnlySnapshotBundle.GetSlotFiltered"/>).
/// </summary>
/// <remarks>
/// Misses are read from a million keys in order, so they miss the CPU caches the way an eth_call's slot reads do; the
/// 32-key arrays of <see cref="ReadOnlySnapshotBundleBenchmark"/> stay cache-hot and understate them.
/// </remarks>
[WarmupCount(3)]
[IterationCount(10)]
public class ReadOnlySnapshotBundleSlotFilterBenchmark
{
    // Unique slots written per in-memory layer of the bundle at mainnet head 26081247, newest first: two small
    // compactions, then 32-block ones. Deeper bundles repeat the pattern.
    private static readonly int[] MainnetLayerSlots = [2332, 5941, 14056, 15362, 14517, 13878];

    private const double BitsPerKey = 14;
    private const int ContractCount = 4_000;
    private const int HotSlotsPerLayer = 64;
    private const int MissCount = 1 << 20;
    private const int HitCount = 1 << 16;
    private const int SameAccountCount = 1 << 12;

    [Params(1, 8, 16, 32)]
    public int Layers;

    [Params(false, true)]
    public bool Filtered;

    private readonly List<FlatSnapshot> _layers = [];
    private IContainer _container = null!;
    private TempPath _dbPath = null!;
    private ReadOnlySnapshotBundle _bundle = null!;
    private SnapshotBundle _snapshotBundle = null!;
    private NoopPersistenceReader _persistence = null!;

    private HashedKey<(Address, UInt256)>[] _hits = null!;
    private HashedKey<(Address, UInt256)>[] _misses = null!;
    private HashedKey<(Address, UInt256)>[] _sameAccount = null!;
    private int _hitIndex;
    private int _missIndex;
    private int _sameAccountIndex;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = TempPath.GetTempDirectory();
        FlatDbConfig config = new() { Enabled = true, CompactionOffset = 0, EnableLongFinality = false };
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(config, new InitConfig { BaseDbPath = _dbPath.Path }))
            .Build();
        IResourcePool resourcePool = _container.Resolve<IResourcePool>();
        ISnapshotCompactor compactor = _container.Resolve<ISnapshotCompactor>();

        Address[] contracts = new Address[ContractCount];
        for (int i = 0; i < contracts.Length; i++) contracts[i] = DeriveAddress(i + 1);
        Address hot = contracts[0];

        Random random = new(42);
        List<HashedKey<(Address, UInt256)>> written = [];
        for (int layer = 0; layer < Layers; layer++)
        {
            SnapshotContent content = resourcePool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
            int slots = MainnetLayerSlots[(Layers - 1 - layer) % MainnetLayerSlots.Length];
            for (int i = 0; i < slots; i++)
            {
                HashedKey<(Address, UInt256)> key = new((contracts[random.Next(1, ContractCount)], (UInt256)(ulong)random.NextInt64()));
                content.Storages[key] = (UInt256)(ulong)(i + 1);
                written.Add(key);
            }

            for (int i = 0; i < HotSlotsPerLayer; i++)
            {
                content.Storages[(hot, (UInt256)(ulong)(layer * HotSlotsPerLayer + i))] = (UInt256)(ulong)(i + 1);
            }

            FlatSnapshot snapshot = new(new StateId((ulong)layer, Keccak.Compute($"{layer}")), new StateId((ulong)layer + 1, Keccak.Compute($"{layer + 1}")),
                content, resourcePool, ResourcePool.Usage.MainBlockProcessing);
            if (layer == Layers - 1)
            {
                // The newest layer of a head bundle is usually a base snapshot, still in its mutable form.
                _layers.Add(snapshot);
            }
            else
            {
                using SnapshotPooledList source = new(1) { snapshot };
                _layers.Add(compactor.CompactSnapshotBundle(source));
            }
        }

        _hits = new HashedKey<(Address, UInt256)>[HitCount];
        for (int i = 0; i < _hits.Length; i++) _hits[i] = written[random.Next(written.Count)];

        _misses = new HashedKey<(Address, UInt256)>[MissCount];
        for (int i = 0; i < _misses.Length; i++) _misses[i] = new((contracts[random.Next(1, ContractCount)], (UInt256)(ulong)random.NextInt64()));

        // A hot contract: half of its reads find a slot some layer wrote, half find none.
        _sameAccount = new HashedKey<(Address, UInt256)>[SameAccountCount];
        for (int i = 0; i < _sameAccount.Length; i++)
        {
            _sameAccount[i] = i % 2 == 0
                ? new((hot, (UInt256)(ulong)random.Next(Layers * HotSlotsPerLayer)))
                : new((hot, (UInt256)(ulong)(1_000_000 + i)));
        }

        _persistence = new NoopPersistenceReader();
        _bundle = CreateBundle();
        _snapshotBundle = new SnapshotBundle(CreateBundle(), new NullTrieNodeCache(), resourcePool,
            ResourcePool.Usage.ReadOnlyProcessingEnv, filterInMemorySlotReads: Filtered);

        // Build the filters now so the read benchmarks measure steady-state reads.
        Read(_misses[0]);
        _snapshotBundle.GetSlot(_misses[0].Key.Item1, _misses[0].Key.Item2, -1, out _);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _snapshotBundle.Dispose();
        _bundle.Dispose();
        foreach (FlatSnapshot layer in _layers) layer.Dispose();
        _container.Dispose();
        _dbPath.Dispose();
    }

    private ReadOnlySnapshotBundle CreateBundle()
    {
        SnapshotPooledList snapshots = new(_layers.Count);
        foreach (FlatSnapshot layer in _layers)
        {
            layer.AcquireLease();
            snapshots.Add(layer);
        }

        return new ReadOnlySnapshotBundle(snapshots, _persistence, recordDetailedMetrics: false, PersistedSnapshotStack.Empty(),
            slotFilterBitsPerKey: Filtered ? BitsPerKey : 0);
    }

    private UInt256? Read(in HashedKey<(Address, UInt256)> key)
    {
        UInt256? value;
        if (Filtered) _bundle.GetSlotFiltered(-1, key, out value);
        else _bundle.GetSlot(-1, key, out value);
        return value;
    }

    [Benchmark]
    public UInt256? GetSlot_Hit() => Read(_hits[_hitIndex++ & (HitCount - 1)]);

    [Benchmark]
    public UInt256? GetSlot_Miss() => Read(_misses[_missIndex++ & (MissCount - 1)]);

    [Benchmark]
    public UInt256? GetSlot_SameAccount() => Read(_sameAccount[_sameAccountIndex++ & (SameAccountCount - 1)]);

    /// <summary>A miss the way <c>FlatStorageTree.Get</c> reads it: key hashing and the bundle's own write buffer first.</summary>
    [Benchmark]
    public UInt256? Bundle_GetSlot_Miss()
    {
        ref readonly HashedKey<(Address, UInt256)> key = ref _misses[_missIndex++ & (MissCount - 1)];
        _snapshotBundle.GetSlot(key.Key.Item1, key.Key.Item2, -1, out UInt256? value);
        return value;
    }

    [Benchmark]
    public UInt256? Bundle_GetSlot_Hit()
    {
        ref readonly HashedKey<(Address, UInt256)> key = ref _hits[_hitIndex++ & (HitCount - 1)];
        _snapshotBundle.GetSlot(key.Key.Item1, key.Key.Item2, -1, out UInt256? value);
        return value;
    }

    /// <summary>The first read of a fresh bundle, which builds the filter when <see cref="Filtered"/>.</summary>
    [Benchmark]
    public UInt256? FirstReadOfFreshBundle()
    {
        using ReadOnlySnapshotBundle bundle = CreateBundle();
        UInt256? value;
        if (Filtered) bundle.GetSlotFiltered(-1, _misses[_missIndex++ & (MissCount - 1)], out value);
        else bundle.GetSlot(-1, _misses[_missIndex++ & (MissCount - 1)], out value);
        return value;
    }
}
