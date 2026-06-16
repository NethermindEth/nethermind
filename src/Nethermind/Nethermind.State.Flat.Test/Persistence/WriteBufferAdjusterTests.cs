// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class WriteBufferAdjusterTests
{
    private const long MinWriteBufferSize = 16 * MemorySizes.MiB;
    private const long AccountMaxWriteBufferSize = 32 * MemorySizes.MiB;
    private const long StorageMaxWriteBufferSize = 64 * MemorySizes.MiB;

    private IColumnsDb<FlatDbColumns> _db = null!;
    private IDb _columnDb = null!;
    private Dictionary<FlatDbColumns, IDb> _columnDbs = null!;
    private StubColumnsWriteBatch _batch = null!;
    private WriteBufferAdjuster _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _db = Substitute.For<IColumnsDb<FlatDbColumns>>();
        _columnDbs = Enum.GetValues<FlatDbColumns>().ToDictionary(c => c, _ => Substitute.For<IDb>());
        _db.GetColumnDb(Arg.Any<FlatDbColumns>()).Returns(call => _columnDbs[call.Arg<FlatDbColumns>()]);
        _columnDb = _columnDbs[FlatDbColumns.Account];

        _batch = new StubColumnsWriteBatch();

        _sut = new WriteBufferAdjuster(_db);
    }

    [TearDown]
    public void TearDown()
    {
        _batch.Dispose();
        foreach (IDb db in _columnDbs.Values) db.Dispose();
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
    [TestCase(200 * MemorySizes.MiB, 0, 1)]
    [TestCase(20 * MemorySizes.MiB, 21 * MemorySizes.MiB, 1)]
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
    public void AdjustWriteBuffer_RespectsPerColumnCap()
    {
        WriteBufferAdjuster.CountingWriteBatch store =
            (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
        store.Set(new byte[200 * MemorySizes.MiB], null);
        _sut.OnBatchDisposed();

        _columnDb.Received(1).SetWriteBuffer(AccountMaxWriteBufferSize);
    }

    [Test]
    public void Wrap_WithDisableWAL_UsesPerColumnCaps()
    {
        _sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.DisableWAL);

        _columnDbs[FlatDbColumns.Account].Received(1).SetWriteBuffer(AccountMaxWriteBufferSize);
        _columnDbs[FlatDbColumns.Storage].Received(1).SetWriteBuffer(StorageMaxWriteBufferSize);
        _columnDbs[FlatDbColumns.StateNodes].Received(1).SetWriteBuffer(StorageMaxWriteBufferSize);
        _columnDbs[FlatDbColumns.StorageNodes].Received(1).SetWriteBuffer(StorageMaxWriteBufferSize);
        _columnDbs[FlatDbColumns.StateTopNodes].DidNotReceive().SetWriteBuffer(Arg.Any<long>());
        _columnDbs[FlatDbColumns.Metadata].DidNotReceive().SetWriteBuffer(Arg.Any<long>());
        _columnDbs[FlatDbColumns.FallbackNodes].DidNotReceive().SetWriteBuffer(Arg.Any<long>());
    }

    [Test]
    public void OnBatchDisposed_WithRaisedFloor_DoesNotShrinkBelowFloor()
    {
        const long floor = 128 * MemorySizes.MiB;
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
        const long floor = 512 * MemorySizes.MiB; // above every per-column cap
        WriteBufferAdjuster sut = new(_db, floor);

        // With floor above the per-column cap, the effective range collapses to [floor, floor]; any write must not
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
