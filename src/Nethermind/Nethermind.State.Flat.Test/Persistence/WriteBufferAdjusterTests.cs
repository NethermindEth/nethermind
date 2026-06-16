// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class WriteBufferAdjusterTests
{
    private const long MinWriteBufferSize = 16L * 1024 * 1024;
    private const long MaxWriteBufferSize = 256L * 1024 * 1024;

    private IColumnsDb<FlatDbColumns> _db = null!;
    private IDb _columnDb = null!;
    private StubColumnsWriteBatch _batch = null!;
    private WriteBufferAdjuster _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _db = Substitute.For<IColumnsDb<FlatDbColumns>>();
        _columnDb = Substitute.For<IDb>();
        _db.GetColumnDb(Arg.Any<FlatDbColumns>()).Returns(_columnDb);

        _batch = new StubColumnsWriteBatch();

        _sut = new WriteBufferAdjuster(_db);
    }

    [TearDown]
    public void TearDown()
    {
        _batch.Dispose();
        _columnDb.Dispose();
        _db.Dispose();
    }

    [Test]
    public void ColumnCount_MatchesEnumValueCount() => Assert.That(Enum.GetValues<FlatDbColumns>().Length, Is.EqualTo(WriteBufferAdjuster.ColumnCount));

    [Test]
    public void Wrap_WithDisableWAL_ReturnsRawBatch()
    {
        IWriteOnlyKeyValueStore result = _sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.DisableWAL);

        Assert.That(result, Is.SameAs(_batch.Inner));
    }

    [Test]
    public void OnBatchDisposed_NoWraps_DoesNotCallSetWriteBuffer()
    {
        _sut.OnBatchDisposed();

        _columnDb.DidNotReceive().SetWriteBuffer(Arg.Any<long>());
    }

    [TestCase(20, 0, 1)]
    [TestCase(200 * 1024 * 1024, 0, 1)]
    [TestCase(20 * 1024 * 1024, 21 * 1024 * 1024, 1)]
    public void OnBatchDisposed_AdjustsWriteBuffer(long firstWriteBytes, long secondWriteBytes, int expectedSetWriteBufferCallCount)
    {
        WriteBufferAdjuster.CountingWriteBatch store = (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
        store.Set(new byte[firstWriteBytes], null);
        _sut.OnBatchDisposed();

        if (secondWriteBytes > 0)
        {
            WriteBufferAdjuster.CountingWriteBatch store2 = (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
            store2.Set(new byte[secondWriteBytes], null);
            _sut.OnBatchDisposed();
        }

        _columnDb.Received(expectedSetWriteBufferCallCount).SetWriteBuffer(Arg.Any<long>());
    }

    [Test]
    public void OnBatchDisposed_WithRaisedFloor_DoesNotShrinkBelowFloor()
    {
        const long floor = 128L * 1024 * 1024;
        WriteBufferAdjuster sut = new(_db, floor);

        // A tiny batch would normally be clamped down to the 16 MB default floor; the configured floor wins.
        WriteBufferAdjuster.CountingWriteBatch store = (WriteBufferAdjuster.CountingWriteBatch)sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
        store.Set(new byte[20], null);
        sut.OnBatchDisposed();

        _columnDb.Received(1).SetWriteBuffer(floor);
    }

    [Test]
    public void OnBatchDisposed_WithFloorAboveCap_AllowsGrowthUpToFloor()
    {
        const long floor = 512L * 1024 * 1024; // above the 256 MB per-batch cap
        WriteBufferAdjuster sut = new(_db, floor);

        // With floor above the growth cap, the effective range collapses to [floor, floor]; any write must not
        // throw (Math.Clamp requires min <= max) and must resolve to the floor.
        WriteBufferAdjuster.CountingWriteBatch store = (WriteBufferAdjuster.CountingWriteBatch)sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
        store.Set(new byte[64], null);
        sut.OnBatchDisposed();

        _columnDb.Received(1).SetWriteBuffer(floor);
    }

    private sealed class StubWriteBatch : IWriteBatch
    {
        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = default) { }
        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = default) { }
        public void Clear() { }
        public void Dispose() { }
    }

    private sealed class StubColumnsWriteBatch : IColumnsWriteBatch<FlatDbColumns>
    {
        public StubWriteBatch Inner { get; } = new();
        public IWriteBatch GetColumnBatch(FlatDbColumns key) => Inner;
        public void Clear() => Inner.Clear();
        public void Dispose() => Inner.Dispose();
    }
}
