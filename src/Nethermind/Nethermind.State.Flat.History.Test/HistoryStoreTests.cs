// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using NUnit.Framework;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Test;

public class HistoryStoreTests
{
    private static readonly byte[] KeyA = [1, 2, 3, 4];
    private static readonly byte[] KeyB = [9, 9, 9, 9];

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columnsDb = null!;
    private HistoryStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _store = new HistoryStore(_columnsDb.GetColumnDb(FlatHistoryColumns.AccountHistory), LimboLogs.Instance.GetClassLogger<HistoryStore>());
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    // KeyA: 0xAA at block 5, 0xBBCC at block 20, deleted at block 30.
    [TestCase(3ul, null)]   // before the first change -> absent at that height
    [TestCase(5ul, "aa")]
    [TestCase(10ul, "aa")]
    [TestCase(19ul, "aa")]
    [TestCase(20ul, "bbcc")]
    [TestCase(25ul, "bbcc")]
    [TestCase(30ul, "")]    // deleted -> tombstone
    [TestCase(35ul, "")]
    public void Resolves_value_as_of_block(ulong block, string? expectedHex)
    {
        Record(5, KeyA, [0xAA]);
        Record(20, KeyA, [0xBB, 0xCC]);
        Record(30, KeyA, ReadOnlySpan<byte>.Empty);

        Span<byte> buffer = stackalloc byte[64];
        int written = _store.TryGetAt(block, KeyA, buffer);

        if (expectedHex is null)
        {
            Assert.That(written, Is.EqualTo(-1));
            return;
        }

        Assert.That(written, Is.GreaterThanOrEqualTo(0));
        Assert.That(buffer[..written], Is.SequenceEqualTo(Convert.FromHexString(expectedHex)));
    }

    [Test]
    public void Floor_seek_does_not_bleed_across_keys()
    {
        Record(5, KeyA, [0xAA]);
        Record(20, KeyB, [0xBB, 0xCC]);

        Span<byte> buffer = stackalloc byte[64];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.TryGetAt(3, KeyA, buffer), Is.EqualTo(-1));    // seek runs past every KeyA row onto KeyB's
            Assert.That(_store.TryGetAt(10, KeyB, buffer), Is.EqualTo(-1));   // KeyB unchanged at/before 10
            Assert.That(_store.TryGetAt(25, KeyA, buffer), Is.EqualTo(1));    // KeyA keeps its own 1-byte value
            Assert.That(_store.TryGetAt(25, KeyB, buffer), Is.EqualTo(2));    // KeyB keeps its own 2-byte value
        }
    }

    // Block 0 sits at the seek range's upper edge under the descending suffix; the genesis cases pin that boundary.
    [TestCase(0ul, 0ul)]
    [TestCase(4ul, 0ul)]
    [TestCase(5ul, 5ul)]
    [TestCase(19ul, 5ul)]
    [TestCase(20ul, 20ul)]
    [TestCase(1000ul, 20ul)]
    public void Reports_block_of_resolved_change(ulong block, ulong expectedFoundAt)
    {
        Record(0, KeyA, [0x0A]);
        Record(5, KeyA, [0xAA]);
        Record(20, KeyA, [0xBB, 0xCC]);

        Span<byte> buffer = stackalloc byte[64];
        Assert.That(_store.TryGetAt(block, KeyA, buffer, out ulong foundAtBlock), Is.GreaterThan(0));
        Assert.That(foundAtBlock, Is.EqualTo(expectedFoundAt));
    }

    // Backward and cross-key seeks are where a tailing iterator re-seeks; the reused snapshot must agree with it.
    [Test]
    public void Reads_at_a_generation_match_tailing_reads_in_any_order([Values] bool flush)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using DbOnTheRocks db = RocksHistoryTestDb.CreateDb(directory.Path);
        HistoryStore store = new(db, LimboLogs.Instance.GetClassLogger<HistoryStore>());
        using (IWriteBatch batch = db.StartWriteBatch())
        {
            store.RecordChange(5, KeyA, [0xAA], batch);
            store.RecordChange(20, KeyA, [0xBB, 0xCC], batch);
            store.RecordChange(30, KeyA, ReadOnlySpan<byte>.Empty, batch);
            store.RecordChange(10, KeyB, [0x0B], batch);
        }
        if (flush) db.Flush();

        (ulong Block, byte[] Key, int Length, ulong FoundAt)[] reads =
        [
            (35, KeyA, 0, 30), (3, KeyA, -1, 0), (25, KeyB, 1, 10), (9, KeyB, -1, 0),
            (20, KeyA, 2, 20), (5, KeyA, 1, 5), (10, KeyB, 1, 10), (19, KeyA, 1, 5),
        ];

        Span<byte> atGeneration = stackalloc byte[64];
        Span<byte> tailing = stackalloc byte[64];
        foreach ((ulong block, byte[] key, int length, ulong foundAt) in reads)
        {
            int expected = store.TryGetAt(block, key, tailing, out ulong expectedAt);
            int actual = store.TryGetAt(block, key, atGeneration, out ulong actualAt, captureGeneration: 7);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(expected, Is.EqualTo(length), $"tailing length at {block}");
                Assert.That(actual, Is.EqualTo(length), $"generation length at {block}");
                Assert.That(expectedAt, Is.EqualTo(foundAt), $"tailing block at {block}");
                Assert.That(actualAt, Is.EqualTo(foundAt), $"generation block at {block}");
                if (length > 0) Assert.That(atGeneration[..actual].ToArray(), Is.EqualTo(tailing[..expected].ToArray()));
            }
        }
    }

    [Test]
    public void A_new_generation_observes_rows_captured_after_the_previous_read([Values] bool flush)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using DbOnTheRocks db = RocksHistoryTestDb.CreateDb(directory.Path);
        HistoryStore store = new(db, LimboLogs.Instance.GetClassLogger<HistoryStore>());
        using (IWriteBatch batch = db.StartWriteBatch()) store.RecordChange(5, KeyA, [0xAA], batch);
        if (flush) db.Flush();

        Span<byte> buffer = stackalloc byte[64];
        Assert.That(store.TryGetAt(30, KeyB, buffer, out _, captureGeneration: 1), Is.EqualTo(-1));

        using (IWriteBatch batch = db.StartWriteBatch()) store.RecordChange(20, KeyB, [0xBB], batch);
        if (flush) db.Flush();

        using (Assert.EnterMultipleScope())
        {
            // The capture that wrote block 20 publishes a new generation before any reader may ask for block 20.
            Assert.That(store.TryGetAt(30, KeyB, buffer, out ulong foundAt, captureGeneration: 2), Is.EqualTo(1), "new generation sees the row");
            Assert.That(foundAt, Is.EqualTo(20UL));
        }
    }

    private void Record(ulong block, ReadOnlySpan<byte> flatKey, ReadOnlySpan<byte> value)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _columnsDb.StartWriteBatch();
        _store.RecordChange(block, flatKey, value, batch.GetColumnBatch(FlatHistoryColumns.AccountHistory));
    }
}
