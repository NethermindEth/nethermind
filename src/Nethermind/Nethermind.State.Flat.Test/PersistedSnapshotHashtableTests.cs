// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Hsst.BTree;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Integration tests that drive the per-address slot-prefix HSST through the production
/// <see cref="PersistedSnapshotBuilder"/> / <see cref="PersistedSnapshotMerger"/> read-back path
/// with partitioning forced on (so the slot-prefix HSST becomes a directory of per-partition
/// <see cref="BTreeNodeKind.Hashtable"/> nodes). Every slot must still round-trip through
/// <see cref="PersistedSnapshot.TryGetSlot"/> after a build and after a merge — i.e. the hashtable
/// probe and the inner-B-tree fallback both resolve correctly across partition boundaries.
/// </summary>
[TestFixture]
public class PersistedSnapshotHashtableTests
{
    private ResourcePool _resourcePool = null!;
    private MemoryArenaManager _memArena = null!;
    private BlobArenaManager _blobs = null!;
    private string _blobsDir = null!;

    // Tiny partition threshold + a 1-key hashtable gate => the ~1500-prefix slot HSST splits into
    // many hashtabled partitions behind a multi-level directory.
    private static readonly HsstBTreeOptions ForcedPartition =
        new() { PartitionThresholdBytes = 2048, HashtableMinKeys = 1 };

    [SetUp]
    public void SetUp()
    {
        _resourcePool = new ResourcePool(new FlatDbConfig());
        // 1500-slot columns + the merged union exceed the 64 KiB default arena.
        _memArena = new MemoryArenaManager(64 * 1024 * 1024);
        _blobsDir = Path.Combine(Path.GetTempPath(), $"nm-pshttest-blobs-{Guid.NewGuid():N}");
        _blobs = new BlobArenaManager(_blobsDir, 4L * 1024 * 1024, PersistedSnapshotTier.Persisted);
    }

    [TearDown]
    public void TearDown()
    {
        _blobs.Dispose();
        _memArena.Dispose();
        try { Directory.Delete(_blobsDir, recursive: true); } catch { /* best-effort */ }
    }

    private PersistedSnapshot CreatePersistedSnapshot(StateId from, StateId to, byte[] data)
    {
        using ArenaWriter writer = _memArena.CreateWriter(data.Length);
        Span<byte> span = writer.GetWriter().GetSpan(data.Length);
        data.CopyTo(span);
        writer.GetWriter().Advance(data.Length);
        (_, ArenaReservation reservation) = writer.Complete();
        TestFixtureHelpers.LeaseBlobIdsFromHsst(reservation, _blobs);
        return new PersistedSnapshot(from, to, reservation, _blobs, PersistedSnapshotTier.Persisted);
    }

    // slot = i << 16 gives each i a distinct 30-byte prefix (the low 2 bytes — the slot suffix —
    // stay 0), so the slot-prefix HSST gets one entry per i.
    private static UInt256 Slot(int i) => (UInt256)i << 16;

    private static void AddSlots(SnapshotContent content, Address addr, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            byte[] v = new byte[32];
            v[28] = (byte)(i >> 16);
            v[29] = (byte)(i >> 8);
            v[30] = (byte)i;
            v[31] = 0x01;
            content.Storages[(addr, Slot(i))] = new SlotValue(v);
        }
    }

    [Test]
    public void Build_ForcedPartition_AllSlots_RoundTrip()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("1"));

        SnapshotContent content = new();
        AddSlots(content, TestItem.AddressA, 1, 1500);

        Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs, ForcedPartition);
        PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        Assert.DoesNotThrow(() =>
            PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted, new PersistedSnapshotBloomFilterManager()));
    }

    [Test]
    public void Merge_ForcedPartition_AllSlots_RoundTrip()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        SnapshotContent content1 = new();
        AddSlots(content1, TestItem.AddressA, 1, 800);
        SnapshotContent content2 = new();
        AddSlots(content2, TestItem.AddressA, 800, 1600);

        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(
            new Snapshot(s0, s1, content1, _resourcePool, ResourcePool.Usage.MainBlockProcessing), _blobs, ForcedPartition);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(
            new Snapshot(s1, s2, content2, _resourcePool, ResourcePool.Usage.MainBlockProcessing), _blobs, ForcedPartition);

        PersistedSnapshotList toMerge = new(2)
        {
            CreatePersistedSnapshot(s0, s1, data1),
            CreatePersistedSnapshot(s1, s2, data2),
        };
        byte[] merged = PersistedSnapshotBuilderTestExtensions.MergeSnapshots(toMerge, ForcedPartition);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s2, merged);

        // The merged snapshot must hold the union of both disjoint slot sets.
        SnapshotContent union = new();
        AddSlots(union, TestItem.AddressA, 1, 1600);
        Snapshot expected = new(s0, s2, union, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        Assert.DoesNotThrow(() =>
            PersistedSnapshotUtils.ValidatePersistedSnapshot(expected, persisted, new PersistedSnapshotBloomFilterManager()));
    }
}
