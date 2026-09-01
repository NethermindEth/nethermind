// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
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
    private ArenaManager _memArena = null!;
    private string _memArenaDir = null!;
    private BlobArenaManager _blobs = null!;
    private string _blobsDir = null!;

    [SetUp]
    public void SetUp()
    {
        _pool = new ResourcePool(new FlatDbConfig());
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
        try { Directory.Delete(_blobsDir, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_memArenaDir, recursive: true); } catch { /* best-effort */ }
    }

    [Test]
    public void TryLoadStateRlp_ReturnsFromPersistedSnapshot_BeforePersistence()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        TreePath path = new(Keccak.Compute("path"), 4);
        byte[] nodeRlp = [0xC2, 0x80, 0x80];

        SnapshotContent content = new();
        content.StateNodes[path] = new TrieNode(NodeType.Leaf, nodeRlp);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] tableData = PersistedSnapshotBuilderTestExtensions.Build(snap, _blobs);

        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s1, tableData);
        PersistedSnapshotList list = new(1) { persisted };

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: AlwaysTrueStack(list));

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
        TreePath path = new(Keccak.Compute("path"), 6);
        byte[] nodeRlp = [0xC1, 0x80];

        SnapshotContent content = new();
        content.StorageNodes[(address, path)] = new TrieNode(NodeType.Branch, nodeRlp);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] tableData = PersistedSnapshotBuilderTestExtensions.Build(snap, _blobs);

        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s1, tableData);
        PersistedSnapshotList list = new(1) { persisted };

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: AlwaysTrueStack(list));

        byte[]? result = bundle.TryLoadStorageRlp(address, path, Keccak.Compute("hash"), ReadFlags.None);

        Assert.That(result, Is.EqualTo(nodeRlp));
        reader.DidNotReceive().TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public void TryLoadStateRlp_FallsThrough_WhenNotInPersistedSnapshot()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        TreePath storedPath = new(Keccak.Compute("stored"), 4);
        TreePath missingPath = new(Keccak.Compute("missing"), 3);
        byte[] nodeRlp = [0xC0];
        byte[] dbRlp = [0xC1, 0x80, 0x80];

        SnapshotContent content = new();
        content.StateNodes[storedPath] = new TrieNode(NodeType.Leaf, nodeRlp);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] tableData = PersistedSnapshotBuilderTestExtensions.Build(snap, _blobs);

        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s1, tableData);
        PersistedSnapshotList list = new(1) { persisted };

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(dbRlp);

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: AlwaysTrueStack(list));

        byte[]? result = bundle.TryLoadStateRlp(missingPath, Keccak.Compute("hash"), ReadFlags.None);

        Assert.That(result, Is.EqualTo(dbRlp));
        reader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public void TryLoadStateRlp_WithoutPersistedSnapshots_GoesDirectlyToPersistence()
    {
        byte[] dbRlp = [0xC0];
        TreePath path = new(Keccak.Compute("path"), 4);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(dbRlp);

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: PersistedSnapshotStack.Empty());

        byte[]? result = bundle.TryLoadStateRlp(path, Keccak.Compute("hash"), ReadFlags.None);

        Assert.That(result, Is.EqualTo(dbRlp));
        reader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TryLoadRlpBatch_PreservesPersistedPrecedence(bool storage)
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        Hash256 address = Keccak.Compute("address");
        TreePath persistedPath = new(Keccak.Compute("persisted"), 4);
        TreePath fallbackPath = new(Keccak.Compute("fallback"), 4);
        byte[] persistedRlp = [0xC2, 0x80, 0x80];
        byte[] databaseRlp = [0xC1, 0x80];

        SnapshotContent content = new();
        if (storage)
            content.StorageNodes[(address, persistedPath)] = new TrieNode(NodeType.Branch, persistedRlp);
        else
            content.StateNodes[persistedPath] = new TrieNode(NodeType.Leaf, persistedRlp);

        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] tableData = PersistedSnapshotBuilderTestExtensions.Build(snap, _blobs);
        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s1, tableData);
        PersistedSnapshotList list = new(1) { persisted };
        BatchPersistenceReader reader = new(databaseRlp);

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: AlwaysTrueStack(list));

        TreePath[] paths = [persistedPath, fallbackPath];
        byte[]?[] values = new byte[]?[paths.Length];
        ReadFlags flags = ReadFlags.HintReadAhead;
        if (storage)
            bundle.TryLoadStorageRlpBatch(address, paths, values, flags);
        else
            bundle.TryLoadStateRlpBatch(paths, values, flags);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(values[0], Is.EqualTo(persistedRlp));
            Assert.That(values[1], Is.EqualTo(databaseRlp));
            Assert.That(reader.BatchCalls, Is.EqualTo(1));
            Assert.That(reader.BatchPaths, Is.EqualTo(new[] { fallbackPath }));
            Assert.That(reader.Flags, Is.EqualTo(flags));
            Assert.That(reader.SingleReads, Is.Zero);
        }
    }

    // Each test snapshot is constructed without a bloom, so it carries the AlwaysTrue
    // placeholder — the stack probes every snapshot unfiltered, which is what these tests want.
    private static PersistedSnapshotStack AlwaysTrueStack(PersistedSnapshotList list) =>
        new(list, recordDetailedMetrics: false);

    private PersistedSnapshot CreatePersistedSnapshot(StateId from, StateId to, byte[] data) =>
        TestFixtureHelpers.CreatePersistedSnapshot(_memArena, _blobs, from, to, data);

    private sealed class BatchPersistenceReader(byte[] databaseRlp) : IPersistence.IPersistenceReader, IBatchedTrieReader
    {
        public int BatchCalls { get; private set; }
        public int SingleReads { get; private set; }
        public List<TreePath> BatchPaths { get; } = [];
        public ReadFlags Flags { get; private set; }

        public Account? GetAccount(Address address) => null;

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue) => false;

        public StateId CurrentState => StateId.PreGenesis;

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags)
        {
            SingleReads++;
            return databaseRlp;
        }

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
        {
            SingleReads++;
            return databaseRlp;
        }

        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => null;

        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) => false;

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
            throw new NotSupportedException();

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            throw new NotSupportedException();

        public bool IsPreimageMode => false;

        public void Dispose() { }

        void IBatchedTrieReader.TryLoadStateRlpBatch(ReadOnlySpan<TreePath> paths, Span<byte[]?> values, ReadFlags flags) =>
            ReadBatch(paths, values, flags);

        void IBatchedTrieReader.TryLoadStorageRlpBatch(Hash256 address, ReadOnlySpan<TreePath> paths, Span<byte[]?> values, ReadFlags flags) =>
            ReadBatch(paths, values, flags);

        private void ReadBatch(ReadOnlySpan<TreePath> paths, Span<byte[]?> values, ReadFlags flags)
        {
            BatchCalls++;
            Flags = flags;
            for (int i = 0; i < paths.Length; i++)
            {
                BatchPaths.Add(paths[i]);
                values[i] = databaseRlp;
            }
        }
    }
}
