// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.State.Flat.BlockRangeTrieForest;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;
using ForestImpl = Nethermind.State.Flat.BlockRangeTrieForest.BlockRangeTrieForest;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class ReadOnlySnapshotBundlePersistedTests
{
    private ResourcePool _pool = null!;
    private MemoryArenaManager _memArena = null!;

    [SetUp]
    public void SetUp()
    {
        _pool = new ResourcePool(new FlatDbConfig());
        _memArena = new MemoryArenaManager();
    }

    [TearDown]
    public void TearDown() => _memArena.Dispose();

    [Test]
    public void TryLoadStateRlp_ReturnsFromForest_BeforePersistence()
    {
        const int blockRangePerForest = 1;
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        TreePath path = new(Keccak.Compute("path"), 4);
        byte[] nodeRlp = [0xC0, 0x80, 0x80];
        Hash256 nodeHash = Keccak.Compute(nodeRlp);

        // Populate forest with the RLP (this is how PersistedSnapshotRepository does it at build time)
        using SnapshotableMemDb forestDb = new();
        ForestImpl forest = new(forestDb);
        using (IBlockRangeTrieForest.IWriter writer = forest.CreateWriter())
        {
            writer.PutState(BlockRangeForestKey.BlockRangeForBlock(s1.BlockNumber, blockRangePerForest), path, nodeHash, nodeRlp);
            writer.Flush();
        }

        // Persisted snapshot only needs to exist to establish block-range bounds for the forest scan
        byte[] hsstData = PersistedSnapshotBuilderTestExtensions.Build(new Snapshot(s0, s1, new SnapshotContent(), _pool, ResourcePool.Usage.MainBlockProcessing));
        PersistedSnapshot persisted = CreatePersistedSnapshot(1, s0, s1, PersistedSnapshotType.Full, hsstData);
        PersistedSnapshotList list = new(1);
        list.Add(persisted);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(s0);

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: list,
            blockRangeTrieForest: forest,
            blockRangePerForest: blockRangePerForest);

        byte[]? result = bundle.TryLoadStateRlp(path, nodeHash, ReadFlags.None);

        Assert.That(result, Is.EqualTo(nodeRlp));
        reader.DidNotReceive().TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public void TryLoadStorageRlp_ReturnsFromForest_BeforePersistence()
    {
        const int blockRangePerForest = 1;
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        Hash256 address = Keccak.Compute("address");
        TreePath path = new(Keccak.Compute("path"), 6);
        byte[] nodeRlp = [0xC1, 0x80];
        Hash256 nodeHash = Keccak.Compute(nodeRlp);

        using SnapshotableMemDb forestDb = new();
        ForestImpl forest = new(forestDb);
        using (IBlockRangeTrieForest.IWriter writer = forest.CreateWriter())
        {
            writer.PutStorage(BlockRangeForestKey.BlockRangeForBlock(s1.BlockNumber, blockRangePerForest), (ValueHash256)address, path, nodeHash, nodeRlp);
            writer.Flush();
        }

        byte[] hsstData = PersistedSnapshotBuilderTestExtensions.Build(new Snapshot(s0, s1, new SnapshotContent(), _pool, ResourcePool.Usage.MainBlockProcessing));
        PersistedSnapshot persisted = CreatePersistedSnapshot(1, s0, s1, PersistedSnapshotType.Full, hsstData);
        PersistedSnapshotList list = new(1);
        list.Add(persisted);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(s0);

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: list,
            blockRangeTrieForest: forest,
            blockRangePerForest: blockRangePerForest);

        byte[]? result = bundle.TryLoadStorageRlp(address, path, nodeHash, ReadFlags.None);

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

        // Build persisted snapshot with one path
        SnapshotContent content = new();
        content.StateNodes[storedPath] = new TrieNode(NodeType.Leaf, nodeRlp);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] hsstData = PersistedSnapshotBuilderTestExtensions.Build(snap);

        PersistedSnapshot persisted = CreatePersistedSnapshot(1, s0, s1, PersistedSnapshotType.Full, hsstData);
        PersistedSnapshotList list = new(1);
        list.Add(persisted);

        // Mock persistence reader returns data for the missing path
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(dbRlp);

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: list,
            blockRangeTrieForest: NullBlockRangeTrieForest.Instance,
            blockRangePerForest: 0);

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

        // Empty persisted snapshots list
        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: PersistedSnapshotList.Empty(),
            blockRangeTrieForest: NullBlockRangeTrieForest.Instance,
            blockRangePerForest: 0);

        byte[]? result = bundle.TryLoadStateRlp(path, Keccak.Compute("hash"), ReadFlags.None);

        Assert.That(result, Is.EqualTo(dbRlp));
        reader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public void TryLoadStateRlp_HitsForest_WhenMissingFromPersistedSnapshot()
    {
        // Arrange a persisted snapshot covering block range 1 (blocks 8192-16383 at BlockRangePerForest=8192)
        const int blockRangePerForest = 8192;
        StateId s0 = new(8192, Keccak.EmptyTreeHash);
        StateId s1 = new(16384, Keccak.Compute("1")); // end of range 1

        // Build an empty persisted snapshot (no trie columns - forest-spilled)
        byte[] hsstData = PersistedSnapshotBuilderTestExtensions.Build(new Snapshot(s0, s1, new SnapshotContent(), _pool, ResourcePool.Usage.MainBlockProcessing));
        PersistedSnapshot persisted = CreatePersistedSnapshot(1, s0, s1, PersistedSnapshotType.Full, hsstData);
        PersistedSnapshotList list = new(1);
        list.Add(persisted);

        // Populate the forest with a state node at block range 1
        using SnapshotableMemDb forestDb = new();
        ForestImpl forest = new(forestDb);
        TreePath path = new(new ValueHash256(Bytes.FromHexString("AABB000000000000000000000000000000000000000000000000000000000000")), 4);
        Hash256 hash = Keccak.Compute("node");
        byte[] nodeRlp = Bytes.FromHexString("C080");
        using (IBlockRangeTrieForest.IWriter writer = forest.CreateWriter())
        {
            writer.PutState(1, path, hash, nodeRlp);
            writer.Flush();
        }

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(new StateId(0, Keccak.EmptyTreeHash)); // below range 0 → lower bound = 0

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: list,
            blockRangeTrieForest: forest,
            blockRangePerForest: blockRangePerForest);

        byte[]? result = bundle.TryLoadStateRlp(path, hash, ReadFlags.None);

        Assert.That(result, Is.EqualTo(nodeRlp));
        reader.DidNotReceive().TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public void TryLoadStateRlp_FallsThroughToReader_WhenNotInForest()
    {
        const int blockRangePerForest = 8192;
        StateId s0 = new(8192, Keccak.EmptyTreeHash);
        StateId s1 = new(16384, Keccak.Compute("1"));

        byte[] hsstData = PersistedSnapshotBuilderTestExtensions.Build(new Snapshot(s0, s1, new SnapshotContent(), _pool, ResourcePool.Usage.MainBlockProcessing));
        PersistedSnapshot persisted = CreatePersistedSnapshot(1, s0, s1, PersistedSnapshotType.Full, hsstData);
        PersistedSnapshotList list = new(1);
        list.Add(persisted);

        using SnapshotableMemDb forestDb = new();
        ForestImpl emptyForest = new(forestDb);

        byte[] dbRlp = Bytes.FromHexString("C1C0");
        TreePath path = new(new ValueHash256(Bytes.FromHexString("CCDD000000000000000000000000000000000000000000000000000000000000")), 3);
        Hash256 hash = Keccak.Compute("missing");

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(new StateId(0, Keccak.EmptyTreeHash));
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(dbRlp);

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: list,
            blockRangeTrieForest: emptyForest,
            blockRangePerForest: blockRangePerForest);

        byte[]? result = bundle.TryLoadStateRlp(path, hash, ReadFlags.None);

        Assert.That(result, Is.EqualTo(dbRlp));
        reader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public void TryLoadStateRlp_ForestScanHonorsLowerBound_SkipsEntriesBelowCurrentState()
    {
        const int blockRangePerForest = 8192;
        // Persisted snapshot covers range 2 (blocks 16384-24575)
        StateId s0 = new(16384, Keccak.EmptyTreeHash);
        StateId s1 = new(24576, Keccak.Compute("2"));

        byte[] hsstData = PersistedSnapshotBuilderTestExtensions.Build(new Snapshot(s0, s1, new SnapshotContent(), _pool, ResourcePool.Usage.MainBlockProcessing));
        PersistedSnapshot persisted = CreatePersistedSnapshot(1, s0, s1, PersistedSnapshotType.Full, hsstData);
        PersistedSnapshotList list = new(1);
        list.Add(persisted);

        using SnapshotableMemDb forestDb = new();
        ForestImpl forest = new(forestDb);

        TreePath path = new(new ValueHash256(Bytes.FromHexString("EEFF000000000000000000000000000000000000000000000000000000000000")), 4);
        Hash256 hash = Keccak.Compute("stale");
        byte[] staleRlp = Bytes.FromHexString("80");

        // Write the node at range 0 (stale — CurrentState is at block 8192 = range 1)
        using (IBlockRangeTrieForest.IWriter writer = forest.CreateWriter())
        {
            writer.PutState(0, path, hash, staleRlp);
            writer.Flush();
        }

        // CurrentState at block 8192 → lower bound = range 1, so range 0 is skipped
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(new StateId(8192, Keccak.Compute("cs")));
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns((byte[]?)null);

        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: list,
            blockRangeTrieForest: forest,
            blockRangePerForest: blockRangePerForest);

        byte[]? result = bundle.TryLoadStateRlp(path, hash, ReadFlags.None);

        // Stale entry in range 0 is not returned; falls through to reader (returns null)
        Assert.That(result, Is.Null);
        reader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    private PersistedSnapshot CreatePersistedSnapshot(int id, StateId from, StateId to, PersistedSnapshotType type, byte[] data)
    {
        using ArenaWriter writer = _memArena.CreateWriter(data.Length);
        Span<byte> span = writer.GetWriter().GetSpan(data.Length);
        data.CopyTo(span);
        writer.GetWriter().Advance(data.Length);
        (_, ArenaReservation reservation) = writer.Complete();
        return new PersistedSnapshot(id, from, to, type, reservation);
    }
}
