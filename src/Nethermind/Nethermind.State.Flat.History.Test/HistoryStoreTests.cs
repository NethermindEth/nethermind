// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using NUnit.Framework;

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
        _store = new HistoryStore(
            _columnsDb.GetColumnDb(FlatHistoryColumns.AccountHistory),
            _columnsDb.GetColumnDb(FlatHistoryColumns.AccountChangeSets));
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    // KeyA: 0xAA at block 5, 0xBBCC at block 20, deleted at block 30.
    [TestCase(3ul, null)]   // before the first change -> caller falls back to the tip
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
        Assert.That(buffer[..written].ToArray(), Is.EqualTo(Convert.FromHexString(expectedHex)));
    }

    [Test]
    public void Floor_seek_does_not_bleed_across_keys()
    {
        Record(5, KeyA, [0xAA]);
        Record(20, KeyB, [0xBB, 0xCC]);

        Span<byte> buffer = stackalloc byte[64];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.TryGetAt(10, KeyB, buffer), Is.EqualTo(-1));   // KeyB unchanged at/before 10
            Assert.That(_store.TryGetAt(25, KeyA, buffer), Is.EqualTo(1));    // KeyA keeps its own 1-byte value
            Assert.That(_store.TryGetAt(25, KeyB, buffer), Is.EqualTo(2));    // KeyB keeps its own 2-byte value
        }
    }

    private void Record(ulong block, ReadOnlySpan<byte> flatKey, ReadOnlySpan<byte> value)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _columnsDb.StartWriteBatch();
        _store.RecordChange(
            block, flatKey, value,
            batch.GetColumnBatch(FlatHistoryColumns.AccountHistory),
            batch.GetColumnBatch(FlatHistoryColumns.AccountChangeSets));
    }
}
