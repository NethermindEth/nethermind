// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.IO;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Core.Test;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

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
        Assert.That(buffer[..written], Is.SequenceEqualTo(Convert.FromHexString(expectedHex)));
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
            Assert.That(buffer[..written], Is.SequenceEqualTo(new byte[] { 0xBB, 0xCC }));
        }
    }

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

    [Test]
    public void SnapshotRead_WithNativeHistory_HandlesMissingEmptyAndOversizedRows(
        [Values(5ul, 20ul, 30ul, ulong.MaxValue)] ulong block,
        [Values(0, 1, 64)] int bufferLength, [Values] bool empty)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        DbConfig config = new();
        RocksDbConfigFactory factory = new(config, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false);
        using DbOnTheRocks db = new(directory.Path, new DbSettings("Blocks", "history"), config, factory, LimboLogs.Instance);
        using SnapshotCeilingDb observed = new(db);
        HistoryStoreV3 store = new(observed);
        using (IWriteBatch batch = db.StartWriteBatch())
        {
            store.RecordPreValue(20, KeyA, empty ? [] : [0xAA], batch);
            store.RecordPreValue(30, KeyA, [0xBB, 0xCC], batch);
            store.RecordPreValue(25, KeyB, [0xDD], batch);
        }

        int expectedLength = block < 20 ? empty ? 0 : 1 : block < 30 ? 2 : -1;
        if (expectedLength > bufferLength)
        {
            Assert.That(() => store.TryGetValueBeforeNextChange(block, KeyA, new byte[bufferLength], out _),
                Throws.TypeOf<StateUnavailableException>());
            Assert.That(observed.CeilingReads, Is.EqualTo(1), "the capability handles the first lookup even when its row is corrupt");
            return;
        }
        byte[] output = new byte[bufferLength];
        int written = store.TryGetValueBeforeNextChange(block, KeyA, output, out ulong next);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(expectedLength));
            Assert.That(observed.CeilingReads, Is.EqualTo(block == ulong.MaxValue ? 0 : 1), "the unscoped lookup uses the snapshot capability");
            Assert.That(next, Is.EqualTo(block < 20 ? 20UL : block < 30 ? 30UL : 0UL));
            if (written > 0) Assert.That(output.AsSpan(0, written), Is.SequenceEqualTo(block < 20 ? new byte[] { 0xAA } : new byte[] { 0xBB, 0xCC }));
        }
    }

    [Test]
    public void ReadScope_WhenCaptureGenerationChanges_RefreshesTheSnapshot([Values] bool flush)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        DbConfig config = new();
        RocksDbConfigFactory factory = new(config, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false);
        using DbOnTheRocks db = new(directory.Path, new DbSettings("Blocks", "history"), config, factory, LimboLogs.Instance);
        HistoryStoreV3 store = new(db);
        using (IWriteBatch batch = db.StartWriteBatch()) store.RecordPreValue(20, KeyA, [0xAA], batch);
        if (flush) db.Flush();
        using HistoryStoreV3.ReadScope scope = new();
        Span<byte> output = stackalloc byte[64];
        Assert.That(store.TryGetValueBeforeNextChange(5, KeyA, output, out _, scope, generation: 0), Is.EqualTo(1));
        Assert.That(output[0], Is.EqualTo(0xAA));
        Assert.That(store.TryGetValueBeforeNextChange(5, KeyB, output, out _, scope, generation: 0), Is.EqualTo(-1));

        using (IWriteBatch batch = db.StartWriteBatch()) store.RecordPreValue(25, KeyB, [0xBB], batch);
        if (flush) db.Flush();
        Assert.That(store.TryGetValueBeforeNextChange(5, KeyB, output, out _, scope, generation: 0), Is.EqualTo(-1));
        Assert.That(store.TryGetValueBeforeNextChange(5, KeyB, output, out ulong next, scope, generation: 1), Is.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(output[0], Is.EqualTo(0xBB));
            Assert.That(next, Is.EqualTo(25UL));
        }
        scope.Dispose();
        Assert.That(() => store.TryGetValueBeforeNextChange(5, KeyA, new byte[64], out _, scope, generation: 1), Throws.TypeOf<ObjectDisposedException>());
    }

    private sealed class SnapshotCeilingDb(ISnapshotSortedKeyValueStore inner) : MemDb, ISnapshotSortedKeyValueStore
    {
        internal int CeilingReads { get; private set; }
        public byte[]? FirstKey => throw new NotSupportedException();
        public byte[]? LastKey => throw new NotSupportedException();
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            throw new InvalidOperationException("An unscoped read must use the snapshot capability.");

        public bool TryGetCeilingInSnapshot(
            scoped ReadOnlySpan<byte> lowerBoundInclusive, scoped ReadOnlySpan<byte> upperBoundExclusive,
            Span<byte> keyBuffer, out int keyLength, Span<byte> valueBuffer, out int valueLength)
        {
            CeilingReads++;
            return inner.TryGetCeilingInSnapshot(lowerBoundInclusive, upperBoundExclusive, keyBuffer, out keyLength, valueBuffer, out valueLength);
        }
    }

    private void Record(ulong block, ReadOnlySpan<byte> flatKey, ReadOnlySpan<byte> valueBeforeChange)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _columnsDb.StartWriteBatch();
        _store.RecordPreValue(block, flatKey, valueBeforeChange, batch.GetColumnBatch(FlatHistoryColumns.AccountHistory));
    }
}
