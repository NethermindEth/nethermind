// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Microbenchmark for <see cref="PersistedSnapshotMerger.NWayMergeSnapshotsWithViews"/> — the
/// dominant cost in persisted-snapshot compaction. Parameterised over N (the snapshot
/// count being merged); at default <c>CompactSize=32</c> the large-tier compactor sees
/// N up to ~32 sources at <c>compactSize=1024</c>. Each synthetic snapshot carries one
/// unique account plus a shared overlapping account with a per-block slot, so the
/// per-address sub-tag merge runs with <c>matchCount == N</c> and the slot merge sees
/// N inputs — exercising the hot paths the optimisation targets.
/// </summary>
[MemoryDiagnoser]
public class PersistedSnapshotCompactBenchmark : IDisposable
{
    [Params(2, 4, 8, 16, 32)]
    public int N { get; set; }

    private string _testDir = null!;
    private ArenaManager _arena = null!;
    private BlobArenaManager _blobs = null!;
    private PersistedSnapshotRepository _repo = null!;
    private ResourcePool _pool = null!;
    private PersistedSnapshotList _snapshots = null!;
    private long _estimatedSize;
    private int _disposed;

    [GlobalSetup]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nm_compact_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        _arena = new ArenaManager(
            Path.Combine(_testDir, "arenas"),
            pageCacheBytes: 0,
            maxArenaSize: 16 * 1024 * 1024);
        _blobs = new BlobArenaManager(
            Path.Combine(_testDir, "blobs"),
            maxFileSize: 16 * 1024 * 1024,
            PersistedSnapshotTier.Persisted);
        _repo = new PersistedSnapshotRepository(
            _arena, _blobs, new MemDb(),
            new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        _repo.LoadFromCatalog();
        _pool = new ResourcePool(new FlatDbConfig());

        StateId prev = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= N; i++)
        {
            StateId next = new(i, Keccak.Compute($"s{i}"));
            SnapshotContent c = new();
            // Unique account per block — exercises non-overlapping merge.
            c.Accounts[TestItem.Addresses[(i - 1) % TestItem.Addresses.Length]] =
                Build.An.Account.WithBalance((UInt256)(i * 100)).TestObject;
            // Shared overlapping account with a per-block slot — drives matchCount == N
            // through NWayMergePerAddressHsst and feeds the slot merge with N inputs.
            c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance((UInt256)i).TestObject;
            c.Storages[(TestItem.AddressA, (UInt256)i)] = new SlotValue(new byte[] { (byte)i });
            _repo.ConvertSnapshotToPersistedSnapshot(
                new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            prev = next;
        }

        // Pre-assemble once; the list holds source leases for the lifetime of the run.
        // The merge opens fresh WholeReadSessions per call so repeated benchmark invocations
        // remain independent.
        _snapshots = _repo.AssembleSnapshotsForCompaction(prev, 0);
        for (int i = 0; i < _snapshots.Count; i++)
            _estimatedSize += _snapshots[i].Size;
    }

    [Benchmark]
    public long Compact()
    {
        // Pooled in-memory writer — discarded each invocation so the merge cost is
        // measured without disk I/O or arena bookkeeping. Initial capacity matches the
        // sum-of-sources upper bound (the same hint PersistedSnapshotCompactor uses).
        using PooledByteBufferWriter pooled = new(checked((int)Math.Min(_estimatedSize, int.MaxValue)));
        int n = _snapshots.Count;
        using ArrayPoolList<WholeReadSession> sessionsList = new(n, n);
        using NativeMemoryListRef<WholeReadSessionView> viewsList = new(n, n);
        WholeReadSession[] sessionArr = sessionsList.UnsafeGetInternalArray();
        Span<WholeReadSessionView> views = viewsList.AsSpan();
        try
        {
            for (int i = 0; i < n; i++)
            {
                sessionArr[i] = _snapshots[i].BeginWholeReadSession();
                views[i] = sessionArr[i].GetView();
            }
            PersistedSnapshotMerger.NWayMergeSnapshotsWithViews<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin>(
                views, ref pooled.GetWriter(), bloom: Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter.AlwaysTrue());
        }
        finally
        {
            for (int i = 0; i < n; i++) sessionArr[i]?.Dispose();
        }
        return pooled.GetWriter().Written;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _snapshots?.Dispose();
        _repo?.Dispose();
        _blobs?.Dispose();
        _arena?.Dispose();
        if (_testDir is not null && Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }
}
