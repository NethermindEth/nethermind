// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

// These tests cover only the store's own forward-seek contract in isolation; the end-to-end as-of-block read
// (including the persisted-flat fallback and self-destruct handling) is covered by HistoryWriterTests instead.
public class HistoryStoreV3Tests
{
    private static readonly byte[] KeyA = [1, 2, 3, 4];
    private static readonly byte[] KeyB = [9, 9, 9, 9];

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columnsDb = null!;
    private HistoryStoreV3 _store = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _store = new HistoryStoreV3(_columnsDb.GetColumnDb(FlatHistoryColumns.AccountHistory));
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    // KeyA's value was 0xAA before the change at block 20, and 0xBBCC before the change at block 30 (i.e. it held
    // 0xAA from some point up to 20, then 0xBBCC from 20 up to 30).
    [TestCase(5ul, "aa", 20ul)]
    [TestCase(19ul, "aa", 20ul)]
    [TestCase(20ul, "bbcc", 30ul)]
    [TestCase(29ul, "bbcc", 30ul)]
    [TestCase(30ul, null, 0ul)]
    [TestCase(35ul, null, 0ul)]
    public void TryGetValueBeforeNextChange_ResolvesTheNearestChangeStrictlyAbove(ulong block, string? expectedHex, ulong expectedNextChangeBlock)
    {
        Record(20, KeyA, [0xAA]);
        Record(30, KeyA, [0xBB, 0xCC]);

        Span<byte> buffer = stackalloc byte[64];
        int written = _store.TryGetValueBeforeNextChange(block, KeyA, buffer, out ulong nextChangeBlock);

        if (expectedHex is null)
        {
            Assert.That(written, Is.EqualTo(-1), "no captured change above the watermark; caller decides what that means for its read");
            return;
        }

        Assert.That(written, Is.GreaterThan(0));
        Assert.That(buffer[..written].ToArray(), Is.EqualTo(Convert.FromHexString(expectedHex)));
        Assert.That(nextChangeBlock, Is.EqualTo(expectedNextChangeBlock));
    }

    [Test]
    public void TryGetValueBeforeNextChange_WithEmptyPreValue_ReturnsZero_MeaningTheKeyDidNotExistBefore()
    {
        Record(10, KeyA, ReadOnlySpan<byte>.Empty);

        Span<byte> buffer = stackalloc byte[64];
        int written = _store.TryGetValueBeforeNextChange(5, KeyA, buffer, out ulong nextChangeBlock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(0), "an empty recorded pre-value means the key's first-ever change is this one");
            Assert.That(nextChangeBlock, Is.EqualTo(10UL));
        }
    }

    [Test]
    public void TryGetValueBeforeNextChange_DoesNotBleedAcrossKeys()
    {
        // KeyA's only change is at 20; KeyB's only change is at 25 (a higher block, chosen so a broken key
        // boundary would show up as KeyA's query wrongly finding KeyB's later row instead of nothing).
        Record(20, KeyA, [0xAA]);
        Record(25, KeyB, [0xBB]);

        Span<byte> buffer = stackalloc byte[64];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_store.TryGetValueBeforeNextChange(22, KeyA, buffer, out _), Is.EqualTo(-1),
                "KeyA's only change is at 20 (not above 22); it must not see KeyB's unrelated change at 25");
            Assert.That(_store.TryGetValueBeforeNextChange(22, KeyB, buffer, out ulong foundAtB), Is.GreaterThan(0),
                "KeyB's own change at 25 is above 22 and must be found");
            Assert.That(foundAtB, Is.EqualTo(25UL));
        }
    }

    [Test]
    public void TryGetValueBeforeNextChange_AtMaxBlockValue_NeverHasANextChange()
    {
        Record(20, KeyA, [0xAA]);

        Span<byte> buffer = stackalloc byte[64];
        Assert.That(_store.TryGetValueBeforeNextChange(ulong.MaxValue, KeyA, buffer, out _), Is.EqualTo(-1));
    }

    [Test]
    public void RecordPreValue_OverwritesAnExistingEntryAtTheSameBlock()
    {
        Record(20, KeyA, [0xAA]);
        Record(20, KeyA, [0xBB, 0xCC]);

        Span<byte> buffer = stackalloc byte[64];
        int written = _store.TryGetValueBeforeNextChange(5, KeyA, buffer, out ulong nextChangeBlock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nextChangeBlock, Is.EqualTo(20UL));
            Assert.That(buffer[..written].ToArray(), Is.EqualTo(new byte[] { 0xBB, 0xCC }));
        }
    }

    // A row wider than the caller's buffer means the row is corrupt (every encoder here caps at the buffer's
    // size) - fail loudly rather than truncate a pre-value into something that reads as a different, wrong value.
    [Test]
    public void TryGetValueBeforeNextChange_ValueWiderThanTheBuffer_ThrowsStateUnavailable()
    {
        Record(20, KeyA, new byte[65]); // wider than the 64-byte buffer used below

        Assert.That(() =>
        {
            Span<byte> buffer = stackalloc byte[64];
            _store.TryGetValueBeforeNextChange(5, KeyA, buffer, out _);
        }, Throws.InstanceOf<InvalidOperationException>());
    }

    private void Record(ulong block, ReadOnlySpan<byte> flatKey, ReadOnlySpan<byte> valueBeforeChange)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _columnsDb.StartWriteBatch();
        _store.RecordPreValue(block, flatKey, valueBeforeChange, batch.GetColumnBatch(FlatHistoryColumns.AccountHistory));
    }
}
