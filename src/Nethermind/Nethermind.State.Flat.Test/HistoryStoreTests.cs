// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class HistoryStoreTests
{
    private static readonly byte[] KeyA = [1, 2, 3, 4];
    private static readonly byte[] KeyB = [9, 9, 9, 9];

    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private HistoryStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _store = new HistoryStore(
            _columnsDb.GetColumnDb(FlatDbColumns.AccountHistory),
            _columnsDb.GetColumnDb(FlatDbColumns.AccountChangeSets));
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    // KeyA: 0xAA at block 5, 0xBBCC at block 20, deleted at block 30.
    [TestCase(3, null)]   // before the first change -> caller falls back to the tip
    [TestCase(5, "aa")]
    [TestCase(10, "aa")]
    [TestCase(19, "aa")]
    [TestCase(20, "bbcc")]
    [TestCase(25, "bbcc")]
    [TestCase(30, "")]    // deleted -> tombstone
    [TestCase(35, "")]
    public void Resolves_value_as_of_block(long block, string? expectedHex)
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

    private void Record(long block, ReadOnlySpan<byte> flatKey, ReadOnlySpan<byte> value)
    {
        using IColumnsWriteBatch<FlatDbColumns> batch = _columnsDb.StartWriteBatch();
        _store.RecordChange(
            block, flatKey, value,
            batch.GetColumnBatch(FlatDbColumns.AccountHistory),
            batch.GetColumnBatch(FlatDbColumns.AccountChangeSets));
    }
}
