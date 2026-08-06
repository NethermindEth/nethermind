// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

// Byte-level chunk splitting (cap boundaries, entry-boundary independence, the >1MB destruct-shaped case) is
// covered directly against ChangesetChunkCodec.EncodeChunked in ChangesetChunkCodecTests - this store is now a
// thin sequential-write/read wrapper around whatever chunks that codec produces, so these tests cover only its
// own concern: writing them under contiguous 0-based indices per block, and reading them back.
public class ChangesetSidecarStoreTests
{
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columnsDb = null!;
    private ChangesetSidecarStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _store = new ChangesetSidecarStore(_columnsDb.GetColumnDb(FlatHistoryColumns.ChangesetSidecar));
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    [Test]
    public void RecordChangeset_SmallEntries_WritesASingleChunk()
    {
        List<ChangesetAccountEntry> entries = [Entry(TestItem.AddressA)];

        Write(1, entries);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.TryGetChunk(1, 0), Is.Not.Null);
            Assert.That(_store.TryGetChunk(1, 1), Is.Null, "small entries must not spill into a second chunk");
        }
    }

    [Test]
    public void RecordChangeset_EmptyEntries_StillWritesChunkZero()
    {
        Write(1, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.TryGetChunk(1, 0), Is.Not.Null,
                "an empty changeset must still be distinguishable from a block that was never recorded");
            Assert.That(_store.TryGetChunk(1, 1), Is.Null);
        }
    }

    // Enough accounts, each with enough slots, that the encoded total well exceeds the store's 1MB cap - every
    // chunk EncodeChunked yields (not just the first) must be written under a contiguous 0-based index.
    [Test]
    public void RecordChangeset_EntriesOverTheCap_WritesEachChunkUnderASequentialIndex()
    {
        List<ChangesetAccountEntry> entries = [];
        for (int account = 0; account < 20; account++)
        {
            List<ChangesetSlotEntry> slots = new(2000);
            for (int i = 0; i < 2000; i++)
            {
                slots.Add(new ChangesetSlotEntry((UInt256)(i + 1), new byte[] { (byte)i, 0xAB }, ReadOnlyMemory<byte>.Empty));
            }
            entries.Add(new ChangesetAccountEntry(AddressAt(account), AccountChanged: true, new byte[] { 0x01 }, ReadOnlyMemory<byte>.Empty, slots));
        }

        Write(7, entries);

        int chunkCount = 0;
        while (_store.TryGetChunk(7, (uint)chunkCount) is not null)
        {
            chunkCount++;
        }

        Assert.That(chunkCount, Is.GreaterThan(1), "20 accounts of 2000 slots each must not fit in a single 1MB chunk");
    }

    [Test]
    public void RecordChangeset_DifferentBlocks_DoNotBleedIntoEachOthersChunks()
    {
        Write(10, [Entry(TestItem.AddressA)]);
        Write(20, [Entry(TestItem.AddressB)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.TryGetChunk(10, 0), Is.Not.Null);
            Assert.That(_store.TryGetChunk(20, 0), Is.Not.Null);
            Assert.That(ChangesetChunkCodec.Decode(_store.TryGetChunk(10, 0)!)[0].Address, Is.EqualTo(TestItem.AddressA));
            Assert.That(ChangesetChunkCodec.Decode(_store.TryGetChunk(20, 0)!)[0].Address, Is.EqualTo(TestItem.AddressB));
        }
    }

    private void Write(ulong block, List<ChangesetAccountEntry> entries)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _columnsDb.StartWriteBatch();
        _store.RecordChangeset(block, entries, batch.GetColumnBatch(FlatHistoryColumns.ChangesetSidecar));
    }

    private static ChangesetAccountEntry Entry(Address address)
    {
        List<ChangesetSlotEntry> slots = [
            new ChangesetSlotEntry(1, new byte[] { 0xAA }, ReadOnlyMemory<byte>.Empty),
            new ChangesetSlotEntry(2, new byte[] { 0xBB }, ReadOnlyMemory<byte>.Empty),
        ];
        return new ChangesetAccountEntry(address, AccountChanged: true, new byte[] { 0x01 }, ReadOnlyMemory<byte>.Empty, slots);
    }

    private static Address AddressAt(int i)
    {
        Span<byte> bytes = stackalloc byte[20];
        bytes[0] = 0xCC;
        bytes[18] = (byte)(i >> 8);
        bytes[19] = (byte)i;
        return new Address(bytes);
    }
}
