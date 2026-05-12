// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

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
    [Ignore("Pre-blob-arena synthetic-bytes test; needs redesign — see blob-arena-pass-3.md")]
    public void TryLoadStateRlp_ReturnsFromPersistedSnapshot_BeforePersistence()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        TreePath path = new(Keccak.Compute("path"), 4);
        byte[] nodeRlp = [0xC0, 0x80, 0x80];

        // Build persisted snapshot with a state trie node
        SnapshotContent content = new();
        content.StateNodes[path] = new TrieNode(NodeType.Leaf, nodeRlp);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] hsstData = PersistedSnapshotBuilderTestExtensions.Build(snap);

        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s1, PersistedSnapshotType.Full, hsstData);
        PersistedSnapshotList list = new(1);
        list.Add(persisted);

        // Mock persistence reader that should NOT be called for this path
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();

        ArrayPoolList<PersistedSnapshotBloom> blooms = new(list.Count);
        for (int i = 0; i < list.Count; i++) blooms.Add(PersistedSnapshotBloom.AlwaysTrue);
        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: list,
            persistedBlooms: blooms);

        byte[]? result = bundle.TryLoadStateRlp(path, Keccak.Compute("hash"), ReadFlags.None);

        Assert.That(result, Is.EqualTo(nodeRlp));
        reader.DidNotReceive().TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    [Ignore("Pre-blob-arena synthetic-bytes test; needs redesign — see blob-arena-pass-3.md")]
    public void TryLoadStorageRlp_ReturnsFromPersistedSnapshot_BeforePersistence()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        Hash256 address = Keccak.Compute("address");
        TreePath path = new(Keccak.Compute("path"), 6);
        byte[] nodeRlp = [0xC1, 0x80];

        // Build persisted snapshot with a storage trie node
        SnapshotContent content = new();
        content.StorageNodes[(address, path)] = new TrieNode(NodeType.Branch, nodeRlp);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] hsstData = PersistedSnapshotBuilderTestExtensions.Build(snap);

        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s1, PersistedSnapshotType.Full, hsstData);
        PersistedSnapshotList list = new(1);
        list.Add(persisted);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();

        ArrayPoolList<PersistedSnapshotBloom> blooms = new(list.Count);
        for (int i = 0; i < list.Count; i++) blooms.Add(PersistedSnapshotBloom.AlwaysTrue);
        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: list,
            persistedBlooms: blooms);

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

        // Build persisted snapshot with one path
        SnapshotContent content = new();
        content.StateNodes[storedPath] = new TrieNode(NodeType.Leaf, nodeRlp);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] hsstData = PersistedSnapshotBuilderTestExtensions.Build(snap);

        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s1, PersistedSnapshotType.Full, hsstData);
        PersistedSnapshotList list = new(1);
        list.Add(persisted);

        // Mock persistence reader returns data for the missing path
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(dbRlp);

        ArrayPoolList<PersistedSnapshotBloom> blooms = new(list.Count);
        for (int i = 0; i < list.Count; i++) blooms.Add(PersistedSnapshotBloom.AlwaysTrue);
        using ReadOnlySnapshotBundle bundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false,
            persistedSnapshots: list,
            persistedBlooms: blooms);

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
            persistedBlooms: new ArrayPoolList<PersistedSnapshotBloom>(0));

        byte[]? result = bundle.TryLoadStateRlp(path, Keccak.Compute("hash"), ReadFlags.None);

        Assert.That(result, Is.EqualTo(dbRlp));
        reader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    private PersistedSnapshot CreatePersistedSnapshot(StateId from, StateId to, PersistedSnapshotType type, byte[] data)
    {
        using ArenaWriter writer = _memArena.CreateWriter(data.Length, ArenaReservationTags.Test);
        Span<byte> span = writer.GetWriter().GetSpan(data.Length);
        data.CopyTo(span);
        writer.GetWriter().Advance(data.Length);
        (_, ArenaReservation reservation) = writer.Complete();
        return new PersistedSnapshot(from, to, reservation, new Dictionary<ushort, BlobArenaFile>());
    }
}
