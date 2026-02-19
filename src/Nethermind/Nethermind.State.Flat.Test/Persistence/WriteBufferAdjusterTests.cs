// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
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
    public void Wrap_WithDisableWAL_ReturnsRawBatch()
    {
        IWriteOnlyKeyValueStore result = _sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.DisableWAL);

        result.Should().BeSameAs(_batch.Inner);
    }

    [Test]
    public void Wrap_WithoutDisableWAL_ReturnsCountingWriteBatch()
    {
        IWriteOnlyKeyValueStore result = _sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);

        result.Should().BeOfType<WriteBufferAdjuster.CountingWriteBatch>();
    }

    [Test]
    public void Wrap_SameColumnTwice_ReturnsSameInstance()
    {
        IWriteOnlyKeyValueStore first = _sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
        IWriteOnlyKeyValueStore second = _sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);

        first.Should().BeSameAs(second);
    }

    [Test]
    public void CountingWriteBatch_Set_CountsBytes()
    {
        var store = (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);

        store.Set(new byte[10], new byte[20]);

        store.BytesWritten.Should().Be(30);
    }

    [Test]
    public void CountingWriteBatch_PutSpan_CountsBytes()
    {
        var store = (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);

        store.PutSpan(new byte[8], new byte[16]);

        store.BytesWritten.Should().Be(24);
    }

    [Test]
    public void OnBatchDisposed_SmallWrite_ClampsToMinWriteBufferSize()
    {
        var store = (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
        store.Set(new byte[10], new byte[10]);

        _sut.OnBatchDisposed();

        _columnDb.Received(1).SetWriteBuffer(MinWriteBufferSize);
    }

    [Test]
    public void OnBatchDisposed_LargeWrite_ClampsToMaxWriteBufferSize()
    {
        var store = (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
        // Write enough so that bytesWritten * 1.5 > MaxWriteBufferSize
        byte[] big = new byte[200 * 1024 * 1024];
        store.Set(big, big);

        _sut.OnBatchDisposed();

        _columnDb.Received(1).SetWriteBuffer(MaxWriteBufferSize);
    }

    [Test]
    public void OnBatchDisposed_NoWraps_DoesNotCallSetWriteBuffer()
    {
        _sut.OnBatchDisposed();

        _columnDb.DidNotReceive().SetWriteBuffer(Arg.Any<long>());
    }

    [Test]
    public void OnBatchDisposed_WithinThreshold_SkipsReAdjustment()
    {
        // First batch: write enough to produce a target above min
        long firstWrite = 20L * 1024 * 1024; // 20 MB -> target = 30 MB
        var store = (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
        store.Set(new byte[firstWrite], null);
        _sut.OnBatchDisposed();
        _columnDb.Received(1).SetWriteBuffer(Arg.Any<long>());

        // Second batch: write a similar amount (within 20% of last target)
        long secondWrite = 21L * 1024 * 1024; // 21 MB -> target = 31.5 MB, within 20% of 30 MB
        var store2 = (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
        store2.Set(new byte[secondWrite], null);
        _sut.OnBatchDisposed();

        // SetWriteBuffer should still have been called only once total
        _columnDb.Received(1).SetWriteBuffer(Arg.Any<long>());
    }

    [Test]
    public void OnBatchDisposed_ClearsCounters()
    {
        var store = (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
        store.Set(new byte[10], new byte[10]);
        _sut.OnBatchDisposed();

        // After clear, wrapping again should return a fresh counter
        var store2 = (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
        store2.Should().NotBeSameAs(store);
        store2.BytesWritten.Should().Be(0);
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
