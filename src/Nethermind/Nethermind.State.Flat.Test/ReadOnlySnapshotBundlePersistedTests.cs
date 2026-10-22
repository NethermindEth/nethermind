// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class ReadOnlySnapshotBundlePersistedTests
{
    private ResourcePool _pool = null!;
    private INodeRecordBuffer _buffer = null!;
    private ArenaManager _memArena = null!;
    private string _memArenaDir = null!;
    private BlobArenaManager _blobs = null!;
    private string _blobsDir = null!;

    [SetUp]
    public void SetUp()
    {
        _pool = new ResourcePool(new FlatDbConfig());
        _buffer = FlatTestHelpers.NewTestBuffer();
        _memArenaDir = Path.Combine(Path.GetTempPath(), $"nm-robtest-arena-{Guid.NewGuid():N}");
        _memArena = TestFixtureHelpers.CreateArenaManager(_memArenaDir);
        _blobsDir = Path.Combine(Path.GetTempPath(), $"nm-robtest-blobs-{Guid.NewGuid():N}");
        _blobs = new BlobArenaManager(_blobsDir, 4L * 1024 * 1024);
    }

    [TearDown]
    public void TearDown()
    {
        _blobs.Dispose();
        _memArena.Dispose();
        _buffer.Dispose();
        try { Directory.Delete(_blobsDir, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_memArenaDir, recursive: true); } catch { /* best-effort */ }
    }

    [Test]
    public void TryLoadStateRlp_ReturnsFromPersistedSnapshot_BeforePersistence()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        TreePath path = FlatTestHelpers.CanonicalPath(Keccak.Compute("path"), 4);
        byte[] nodeRlp = FlatTestHelpers.LeafRlp(0xC2, 0x80, 0x80);

        INodeRecordBufferPin bufferPin = _buffer.PinHead();
        SnapshotContent content = new();
        content.SetStateNodeRlp(_buffer, path, nodeRlp);
        using Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing, bufferPin);
        byte[] tableData = PersistedSnapshotBuilderTestExtensions.Build(snap, _blobs);

        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s1, tableData);
        PersistedSnapshotList list = new(1) { persisted };

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: AlwaysTrueStack(list), bufferPin: null);

        byte[]? result = bundle.TryLoadStateRlp(path, Keccak.Compute("hash"), ReadFlags.None);

        Assert.That(result, Is.EqualTo(nodeRlp));
        reader.DidNotReceive().TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public void TryLoadStorageRlp_ReturnsFromPersistedSnapshot_BeforePersistence()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        Hash256 address = Keccak.Compute("address");
        TreePath path = FlatTestHelpers.CanonicalPath(Keccak.Compute("path"), 6);
        byte[] nodeRlp = FlatTestHelpers.LeafRlp(0xC1, 0x80);

        INodeRecordBufferPin bufferPin = _buffer.PinHead();
        SnapshotContent content = new();
        content.SetStorageNodeRlp(_buffer, address, path, nodeRlp);
        using Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing, bufferPin);
        byte[] tableData = PersistedSnapshotBuilderTestExtensions.Build(snap, _blobs);

        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s1, tableData);
        PersistedSnapshotList list = new(1) { persisted };

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: AlwaysTrueStack(list), bufferPin: null);

        byte[]? result = bundle.TryLoadStorageRlp(address, path, Keccak.Compute("hash"), ReadFlags.None);

        Assert.That(result, Is.EqualTo(nodeRlp));
        reader.DidNotReceive().TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public void TryLoadStateRlp_FallsThrough_WhenNotInPersistedSnapshot()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        TreePath storedPath = FlatTestHelpers.CanonicalPath(Keccak.Compute("stored"), 4);
        TreePath missingPath = FlatTestHelpers.CanonicalPath(Keccak.Compute("missing"), 3);
        byte[] nodeRlp = FlatTestHelpers.LeafRlp(0xC0);
        byte[] dbRlp = [0xC1, 0x80, 0x80];

        INodeRecordBufferPin bufferPin = _buffer.PinHead();
        SnapshotContent content = new();
        content.SetStateNodeRlp(_buffer, storedPath, nodeRlp);
        using Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing, bufferPin);
        byte[] tableData = PersistedSnapshotBuilderTestExtensions.Build(snap, _blobs);

        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s1, tableData);
        PersistedSnapshotList list = new(1) { persisted };

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(dbRlp);

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: AlwaysTrueStack(list), bufferPin: null);

        byte[]? result = bundle.TryLoadStateRlp(missingPath, Keccak.Compute("hash"), ReadFlags.None);

        Assert.That(result, Is.EqualTo(dbRlp));
        reader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public void TryLoadStateRlp_WithoutPersistedSnapshots_GoesDirectlyToPersistence()
    {
        byte[] dbRlp = [0xC0];
        TreePath path = FlatTestHelpers.CanonicalPath(Keccak.Compute("path"), 4);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(dbRlp);

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: PersistedSnapshotStack.Empty(), bufferPin: null);

        byte[]? result = bundle.TryLoadStateRlp(path, Keccak.Compute("hash"), ReadFlags.None);

        Assert.That(result, Is.EqualTo(dbRlp));
        reader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public void Span_and_array_rlp_reads_agree([Values] bool storage, [Values(3, 10, 40)] int pathLength)
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        Hash256 address = Keccak.Compute("address");
        TreePath dbPath = FlatTestHelpers.CanonicalPath(Keccak.Compute("db"), pathLength);
        TreePath persistedPath = FlatTestHelpers.CanonicalPath(Keccak.Compute("persisted"), pathLength);
        TreePath missingPath = FlatTestHelpers.CanonicalPath(Keccak.Compute("missing"), pathLength);
        byte[] dbRlp = Bytes.FromHexString("c3018080");
        byte[] persistedRlp = Bytes.FromHexString(FlatTestHelpers.LeafRlpB);

        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance, NullTrieNodeLog.Instance);
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            if (storage) batch.SetStorageTrieNode(address, dbPath, dbRlp);
            else batch.SetStateTrieNode(dbPath, dbRlp);
        }

        INodeRecordBufferPin bufferPin = _buffer.PinHead();
        SnapshotContent content = new();
        if (storage) content.SetStorageNodeRlp(_buffer, address, persistedPath, persistedRlp);
        else content.SetStateNodeRlp(_buffer, persistedPath, persistedRlp);
        using Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing, bufferPin);
        PersistedSnapshotList list = new(1) { CreatePersistedSnapshot(s0, s1, PersistedSnapshotBuilderTestExtensions.Build(snap, _blobs)) };

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            persistence.CreateReader(),
            recordDetailedMetrics: false,
            persistedSnapshots: AlwaysTrueStack(list), bufferPin: null);

        foreach ((TreePath path, byte[]? expected) in new[] { (dbPath, dbRlp), (persistedPath, persistedRlp), (missingPath, (byte[]?)null) })
        {
            byte[]? arrayRlp = storage
                ? bundle.TryLoadStorageRlp(address, path, Keccak.Compute("hash"), ReadFlags.None)
                : bundle.TryLoadStateRlp(path, Keccak.Compute("hash"), ReadFlags.None);

            StrongBox<byte[]?> spanRlp = new();
            bool found = bundle.TryLoadPersistedRlp(storage ? address : null, path, ReadFlags.None, static (rlp, box) => box.Value = rlp.ToArray(), spanRlp);

            Assert.That(arrayRlp, Is.EqualTo(expected), path.ToString());
            Assert.That(found, Is.EqualTo(expected is not null), path.ToString());
            Assert.That(spanRlp.Value, Is.EqualTo(expected), path.ToString());
        }

        const int reads = 64;
        Hash256 expectedHash = Keccak.Compute("hash");
        MeasureReads(owned: true);
        MeasureReads(owned: false);
        long ownedBytes = MeasureReads(owned: true);
        long borrowedBytes = MeasureReads(owned: false);
        Assert.That(ownedBytes - borrowedBytes, Is.GreaterThanOrEqualTo(reads * persistedRlp.Length),
            "callback reads must avoid the owned RLP array, not merely hide it behind the callback API");

        long MeasureReads(bool owned)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < reads; i++)
            {
                if (owned)
                {
                    byte[]? rlp = storage
                        ? bundle.TryLoadStorageRlp(address, persistedPath, expectedHash, ReadFlags.None)
                        : bundle.TryLoadStateRlp(persistedPath, expectedHash, ReadFlags.None);
                    GC.KeepAlive(rlp);
                }
                else
                {
                    bool found = bundle.TryLoadPersistedRlp(storage ? address : null, persistedPath, ReadFlags.None, CheckRlp, persistedRlp.AsSpan());
                    if (!found) throw new InvalidOperationException("Missing persisted RLP");
                }
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        static void CheckRlp(ReadOnlySpan<byte> rlp, Span<byte> expected)
        {
            if (!rlp.SequenceEqual(expected)) throw new InvalidOperationException("Incorrect persisted RLP");
        }
    }

    [Test]
    public void Warm_up_reuses_borrowed_rlp_from_persisted_snapshots([Values] bool storage)
    {
        Hash256 address = Keccak.Compute("address");
        byte[] nodeRlp = FlatTestHelpers.LeafRlp(0x80);
        ValueHash256 hash = ValueKeccak.Compute(nodeRlp);
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, new Hash256(hash));
        INodeRecordBufferPin retain = _buffer.PinHead();
        SnapshotContent content = new();
        if (storage) content.SetStorageNodeRlp(_buffer, address, TreePath.Empty, nodeRlp);
        else content.SetStateNodeRlp(_buffer, TreePath.Empty, nodeRlp);
        using Snapshot snapshot = new(from, to, content, _pool, ResourcePool.Usage.MainBlockProcessing, retain);
        PersistedSnapshotList list = new(1) { CreatePersistedSnapshot(from, to, PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs)) };
        using INodeRecordBuffer warmedBuffer = FlatTestHelpers.NewTestBuffer();
        using SnapshotBundle bundle = new(
            new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new NoopPersistenceReader(), false, AlwaysTrueStack(list), bufferPin: null),
            warmedBuffer, _pool, ResourcePool.Usage.MainBlockProcessing);

        bundle.WarmUp(storage ? address : null, (hash, 0), ValueKeccak.Zero, forDelete: false);

        Assert.That(FlatTestHelpers.LoadRlp((action, nodes) => bundle.TryLoadTrieNodeRecord(storage ? address : null, TreePath.Empty, action, nodes)), Is.EqualTo(nodeRlp));
    }

    // Each test snapshot is constructed without a bloom, so it carries the AlwaysTrue
    // placeholder — the stack probes every snapshot unfiltered, which is what these tests want.
    private static PersistedSnapshotStack AlwaysTrueStack(PersistedSnapshotList list) =>
        new(list, recordDetailedMetrics: false);

    private PersistedSnapshot CreatePersistedSnapshot(StateId from, StateId to, byte[] data) =>
        TestFixtureHelpers.CreatePersistedSnapshot(_memArena, _blobs, from, to, data);
}
