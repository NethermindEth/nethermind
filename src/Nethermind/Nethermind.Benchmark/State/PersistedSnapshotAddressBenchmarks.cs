// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;
using static Nethermind.Benchmarks.State.FlatWorldStateBenchmarkHarness;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// The per-address work of persisted snapshots: building one from a block of <see cref="Addresses"/> changed accounts
/// (half of them with <see cref="SlotsPerContract"/> slots), building its bloom, and merging two such blocks.
/// </summary>
[MemoryDiagnoser]
public class PersistedSnapshotAddressBenchmarks
{
    private const int Addresses = 10_000;
    private const int SlotsPerContract = 4;

    private TempPath _dbPath = null!;
    private IContainer _container = null!;
    private FlatSnapshot _snapshot = null!;
    private BlobArenaWriter _blobWriter = null!;
    private BloomFilter _bloom = null!;
    private PersistedSnapshotList _persisted = null!;
    private byte[] _output = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = TempPath.GetTempDirectory();
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true }, new InitConfig { BaseDbPath = _dbPath.Path }))
            .AddSingleton<IPersistedSnapshotCompactor>(NullPersistedSnapshotCompactor.Instance)
            .Build();
        ResourcePool resourcePool = _container.Resolve<ResourcePool>();
        ISnapshotRepository repository = _container.Resolve<ISnapshotRepository>();
        // The test module turns off long finality, so the interface resolves to the no-op loader.
        PersistedSnapshotLoader loader = _container.Resolve<PersistedSnapshotLoader>();
        StateId genesis = new(0, Keccak.EmptyTreeHash);
        StateId first = new(1, Keccak.Compute("1"));
        StateId second = new(2, Keccak.Compute("2"));
        loader.Load();

        _snapshot = CreateSnapshot(resourcePool, genesis, first, balance: 1);
        using (FlatSnapshot next = CreateSnapshot(resourcePool, first, second, balance: 2))
        {
            loader.ConvertAndRegister(_snapshot);
            loader.ConvertAndRegister(next);
        }

        _persisted = repository.LeaseBaseSnapshotsInRange(genesis, second);
        _output = new byte[(int)(2 * PersistedSnapshotBuilder.EstimateSize(_snapshot))];
        _blobWriter = _container.Resolve<BlobArenaManager>().CreateWriter(PersistedSnapshotBuilder.EstimateTotalRlpSize(_snapshot));
        _bloom = new BloomFilter(Addresses * (SlotsPerContract + 1), 10);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bloom.Dispose();
        _blobWriter.Dispose();
        _persisted.Dispose();
        _snapshot.Dispose();
        _container.Dispose();
        _dbPath.Dispose();
    }

    [Benchmark]
    public long Build()
    {
        BufferWriter writer = new(_output);
        PersistedSnapshotBuilder.Build(_snapshot, ref writer, _blobWriter, _bloom);
        return writer.Written;
    }

    [Benchmark]
    public bool BuildBloom()
    {
        using WholeReadSession session = _persisted[0].BeginWholeReadSession();
        using BloomFilter bloom = PersistedSnapshotBloomBuilder.Build(session, _persisted[0], 10, out bool hasStorageClears);
        return hasStorageClears;
    }

    [Benchmark]
    public bool Merge()
    {
        using WholeReadSession older = _persisted[0].BeginWholeReadSession();
        using WholeReadSession newer = _persisted[1].BeginWholeReadSession();
        BufferWriter writer = new(_output);
        return PersistedSnapshotMerger.NWayMergeSnapshots<BufferWriter, WholeReadSession, WholeReadSessionReader, NoOpPin>(
            [older, newer], ref writer, _bloom);
    }

    private static FlatSnapshot CreateSnapshot(ResourcePool resourcePool, StateId from, StateId to, ulong balance)
    {
        FlatSnapshot snapshot = resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.MainBlockProcessing);
        for (int i = 0; i < Addresses; i++)
        {
            Address address = DeriveAddress(i);
            snapshot.Content.Accounts[address] = new Account((ulong)i, balance);
            if (i % 2 == 0) continue;
            for (int slot = 0; slot < SlotsPerContract; slot++)
                snapshot.Content.Storages[(address, (UInt256)(ulong)slot)] = balance;
        }

        return snapshot;
    }

    private struct BufferWriter(byte[] buffer) : IByteBufferWriter
    {
        public Span<byte> GetSpan(int sizeHint) => buffer.AsSpan((int)Written);
        public void Advance(int count) => Written += count;
        public long Written { get; private set; }
        public readonly long FirstOffset => 0;
    }
}
