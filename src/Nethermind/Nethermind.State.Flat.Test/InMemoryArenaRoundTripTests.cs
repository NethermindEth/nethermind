// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;
using NUnit.Framework;
using WholeReadScanner = Nethermind.State.Flat.PersistedSnapshots.PersistedSnapshotScanner<
    Nethermind.State.Flat.PersistedSnapshots.Storage.WholeReadSession,
    Nethermind.State.Flat.PersistedSnapshots.Storage.WholeReadSessionReader,
    Nethermind.State.Flat.Io.NoOpPin>;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Runs the persisted-snapshot round trip against the RAM-backed arena backings
/// (<see cref="InMemoryArenaManager"/> / <see cref="InMemoryBlobArenaManager"/>) instead of the
/// on-disk ones, exercising the native-buffer write path plus both read paths (point reads via
/// <see cref="ArenaByteReader"/> and the whole-read session via <see cref="WholeReadSession"/>).
/// </summary>
[TestFixture]
public class InMemoryArenaRoundTripTests
{
    private ResourcePool _resourcePool = null!;
    private InMemoryArenaManager _arena = null!;
    private InMemoryBlobArenaManager _blobs = null!;

    [SetUp]
    public void SetUp()
    {
        _resourcePool = new ResourcePool(new FlatDbConfig());
        _arena = new InMemoryArenaManager();
        _blobs = new InMemoryBlobArenaManager(4L * 1024 * 1024);
    }

    [TearDown]
    public void TearDown()
    {
        _blobs.Dispose();
        _arena.Dispose();
    }

    private static void PopulateAllDataTypes(SnapshotContent c)
    {
        c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(12345).WithNonce(7).TestObject;
        c.Accounts[TestItem.AddressB] = Build.An.Account
            .WithBalance(0).WithNonce(0)
            .WithCode([0x60, 0x00])
            .WithStorageRoot(Keccak.Compute("storage")).TestObject;
        c.Accounts[TestItem.AddressC] = null;

        byte[] slotVal1 = new byte[32]; slotVal1[31] = 0xFF;
        byte[] slotVal2 = new byte[32]; slotVal2[0] = 0x01; slotVal2[31] = 0x02;
        c.Storages[(TestItem.AddressA, (UInt256)1)] = new SlotValue(slotVal1);
        c.Storages[(TestItem.AddressA, (UInt256)2)] = new SlotValue(slotVal2);
        c.Storages[(TestItem.AddressB, (UInt256)42)] = null;

        c.SelfDestructedStorageAddresses[TestItem.AddressD] = false;
        c.SelfDestructedStorageAddresses[TestItem.AddressE] = true;

        c.StateNodes[new TreePath(Keccak.Compute("tp"), 3)] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
        c.StateNodes[new TreePath(Keccak.Compute("sp"), 8)] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x80]);
        c.StateNodes[new TreePath(Keccak.Compute("lp"), 20)] = new TrieNode(NodeType.Extension, [0xC2, 0x80, 0x81]);

        Hash256 storageAddr = Keccak.Compute("storageAddr");
        c.StorageNodes[(storageAddr, new TreePath(Keccak.Compute("tsp"), 3))] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
        c.StorageNodes[(storageAddr, new TreePath(Keccak.Compute("ssp"), 6))] = new TrieNode(NodeType.Branch, [0xC1, 0x80]);
        c.StorageNodes[(storageAddr, new TreePath(Keccak.Compute("lsp"), 18))] = new TrieNode(NodeType.Leaf, [0xC3, 0x80, 0x81, 0x82]);
    }

    [Test]
    public void RoundTrip_AllDataTypes_ValidatesAgainstSource()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("inmem"));

        SnapshotContent content = new();
        PopulateAllDataTypes(content);

        Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = TestFixtureHelpers.CreatePersistedSnapshot(_arena, _blobs, from, to, data);

        // Point-read path: full replay of every entry kind back out of the RAM arena + blob buffers.
        Assert.DoesNotThrow(() => PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted));
    }

    [Test]
    public void RoundTrip_WholeReadSession_ScansSlotsFromRam()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("inmem-scan"));

        byte[] small = new byte[32]; small[31] = 0x05;
        byte[] high = new byte[32]; high[31] = 0xFF;
        byte[] full = new byte[32];
        for (int i = 0; i < 32; i++) full[i] = (byte)(i + 1);

        SnapshotContent content = new();
        content.Storages[(TestItem.AddressA, (UInt256)1)] = new SlotValue(small);
        content.Storages[(TestItem.AddressA, (UInt256)2)] = new SlotValue(high);
        content.Storages[(TestItem.AddressA, (UInt256)3)] = null;
        content.Storages[(TestItem.AddressB, (UInt256)4)] = new SlotValue(full);

        Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = TestFixtureHelpers.CreatePersistedSnapshot(_arena, _blobs, from, to, data);

        Dictionary<(Address, UInt256), SlotValue?> scanned = [];
        // Whole-read path: exercises the RAM MmapWholeView (null-accessor) branch.
        using (WholeReadSession session = persisted.BeginWholeReadSession())
        {
            WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, persisted);
            foreach (WholeReadScanner.PerAddressEntry entry in scanner.PerAddresses)
                foreach (WholeReadScanner.SlotEntry slot in entry.Slots)
                    scanned[(entry.Address, slot.Slot)] = slot.Value;
        }

        Assert.That(scanned[(TestItem.AddressA, (UInt256)1)]!.Value.AsReadOnlySpan.ToArray(), Is.EqualTo(small));
        Assert.That(scanned[(TestItem.AddressA, (UInt256)2)]!.Value.AsReadOnlySpan.ToArray(), Is.EqualTo(high));
        Assert.That(scanned[(TestItem.AddressA, (UInt256)3)], Is.Null, "deleted slot must surface as null");
        Assert.That(scanned[(TestItem.AddressB, (UInt256)4)]!.Value.AsReadOnlySpan.ToArray(), Is.EqualTo(full));
    }

    [Test]
    public void Arena_RawWriteReadRoundTrip_ThroughReservation()
    {
        byte[] payload = new byte[8000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + 7);

        SnapshotLocation location;
        ArenaReservation reservation;
        using (ArenaWriter writer = _arena.CreateWriter(payload.Length))
        {
            Span<byte> span = writer.GetWriter().GetSpan(payload.Length);
            payload.CopyTo(span);
            writer.GetWriter().Advance(payload.Length);
            (location, reservation) = writer.Complete();
        }

        try
        {
            Assert.That(reservation.Size, Is.EqualTo(payload.Length));

            // Point reader over the native buffer.
            ArenaByteReader reader = reservation.CreateReader();
            byte[] readBack = new byte[payload.Length];
            Assert.That(reader.TryRead(0, readBack), Is.True);
            Assert.That(readBack, Is.EqualTo(payload));

            // Re-open the same location and read again — the RAM arena resolves it like the disk one.
            using ArenaReservation reopened = _arena.Open(location);
            ArenaByteReader reopenedReader = reopened.CreateReader();
            byte[] readBack2 = new byte[payload.Length];
            Assert.That(reopenedReader.TryRead(0, readBack2), Is.True);
            Assert.That(readBack2, Is.EqualTo(payload));
        }
        finally
        {
            reservation.Dispose();
        }
    }
}
