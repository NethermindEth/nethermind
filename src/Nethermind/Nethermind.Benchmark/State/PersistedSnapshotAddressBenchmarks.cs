// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
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

    private string _directory = null!;
    private ArenaManager _arena = null!;
    private BlobArenaManager _blobs = null!;
    private SnapshotRepository _repository = null!;
    private FlatSnapshot _snapshot = null!;
    private BlobArenaWriter _blobWriter = null!;
    private BloomFilter _bloom = null!;
    private PersistedSnapshotList _persisted = null!;
    private byte[] _output = null!;

    [GlobalSetup]
    public void Setup()
    {
        FlatDbConfig config = new();
        ResourcePool resourcePool = new(config);
        StateId genesis = new(0, Keccak.EmptyTreeHash);
        StateId first = new(1, Keccak.Compute("1"));
        StateId second = new(2, Keccak.Compute("2"));

        _directory = Path.Combine(Path.GetTempPath(), $"{nameof(PersistedSnapshotAddressBenchmarks)}-{Guid.NewGuid():N}");
        _arena = new ArenaManager(Path.Combine(_directory, "arena"), config, NullLogManager.Instance);
        _blobs = new BlobArenaManager(Path.Combine(_directory, "blob"), config.ArenaFileSizeBytes);
        SnapshotCatalog catalog = new(new MemDb());
        _repository = new SnapshotRepository(_arena, _blobs, catalog, config, UnavailableStateHeaderProvider.Instance, NullLogManager.Instance);
        PersistedSnapshotLoader loader = new(_repository, _arena, _blobs, catalog, config, NullLogManager.Instance);
        loader.Load();

        _snapshot = CreateSnapshot(resourcePool, genesis, first, balance: 1);
        using (FlatSnapshot next = CreateSnapshot(resourcePool, first, second, balance: 2))
        {
            loader.ConvertAndRegister(_snapshot);
            loader.ConvertAndRegister(next);
        }

        _persisted = _repository.LeaseBaseSnapshotsInRange(genesis, second);
        _output = new byte[(int)(2 * PersistedSnapshotBuilder.EstimateSize(_snapshot))];
        _blobWriter = _blobs.CreateWriter(PersistedSnapshotBuilder.EstimateTotalRlpSize(_snapshot));
        _bloom = new BloomFilter(Addresses * (SlotsPerContract + 1), 10);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bloom.Dispose();
        _blobWriter.Dispose();
        _persisted.Dispose();
        _snapshot.Dispose();
        _repository.Dispose();
        _blobs.Dispose();
        _arena.Dispose();
        Directory.Delete(_directory, recursive: true);
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
