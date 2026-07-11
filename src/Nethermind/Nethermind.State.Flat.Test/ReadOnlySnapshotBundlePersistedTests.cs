// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
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
    public void TryLoadStateRlpMatching_SkipsNewerPersistedNodeWithDifferentHash()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        TreePath path = new(Keccak.Compute("path"), 4);
        byte[] requestedRlp = [0xC2, 0x80, 0x80];
        byte[] newerRlp = [0xC2, 0x01, 0x80];

        SnapshotContent requestedContent = new();
        requestedContent.StateNodes[path] = new TrieNode(NodeType.Leaf, requestedRlp);
        using Snapshot requestedSnapshot = new(s0, s1, requestedContent, _pool, ResourcePool.Usage.MainBlockProcessing);
        SnapshotContent newerContent = new();
        newerContent.StateNodes[path] = new TrieNode(NodeType.Leaf, newerRlp);
        using Snapshot newerSnapshot = new(s1, s2, newerContent, _pool, ResourcePool.Usage.MainBlockProcessing);

        PersistedSnapshotList list = new(2)
        {
            CreatePersistedSnapshot(s0, s1, PersistedSnapshotBuilderTestExtensions.Build(requestedSnapshot, _blobs)),
            CreatePersistedSnapshot(s1, s2, PersistedSnapshotBuilderTestExtensions.Build(newerSnapshot, _blobs)),
        };

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            Substitute.For<IPersistence.IPersistenceReader>(),
            recordDetailedMetrics: false,
            persistedSnapshots: AlwaysTrueStack(list));

        byte[]? result = bundle.TryLoadStateRlpMatching(path, Keccak.Compute(requestedRlp), ReadFlags.None);

        Assert.That(result, Is.EqualTo(requestedRlp));
    }

    [Test]
    public void TryLoadStorageRlpMatching_SkipsNewerPersistedNodeWithDifferentHash()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        Hash256 address = Keccak.Compute("address");
        TreePath path = new(Keccak.Compute("path"), 6);
        byte[] requestedRlp = [0xC2, 0x80, 0x80];
        byte[] newerRlp = [0xC2, 0x01, 0x80];

        SnapshotContent requestedContent = new();
        requestedContent.StorageNodes[(address, path)] = new TrieNode(NodeType.Branch, requestedRlp);
        using Snapshot requestedSnapshot = new(s0, s1, requestedContent, _pool, ResourcePool.Usage.MainBlockProcessing);
        SnapshotContent newerContent = new();
        newerContent.StorageNodes[(address, path)] = new TrieNode(NodeType.Branch, newerRlp);
        using Snapshot newerSnapshot = new(s1, s2, newerContent, _pool, ResourcePool.Usage.MainBlockProcessing);

        PersistedSnapshotList list = new(2)
        {
            CreatePersistedSnapshot(s0, s1, PersistedSnapshotBuilderTestExtensions.Build(requestedSnapshot, _blobs)),
            CreatePersistedSnapshot(s1, s2, PersistedSnapshotBuilderTestExtensions.Build(newerSnapshot, _blobs)),
        };

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            Substitute.For<IPersistence.IPersistenceReader>(),
            recordDetailedMetrics: false,
            persistedSnapshots: AlwaysTrueStack(list));

        byte[]? result = bundle.TryLoadStorageRlpMatching(
            address,
            path,
            Keccak.Compute(requestedRlp),
            ReadFlags.None);

        Assert.That(result, Is.EqualTo(requestedRlp));
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

    // Each test snapshot is constructed without a bloom, so it carries the AlwaysTrue
    // placeholder — the stack probes every snapshot unfiltered, which is what these tests want.
    private static PersistedSnapshotStack AlwaysTrueStack(PersistedSnapshotList list) =>
        new(list, recordDetailedMetrics: false);

    private PersistedSnapshot CreatePersistedSnapshot(StateId from, StateId to, byte[] data) =>
        TestFixtureHelpers.CreatePersistedSnapshot(_memArena, _blobs, from, to, data);
}
