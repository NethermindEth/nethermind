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
        var store = (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
        store.Set(new byte[firstWriteBytes], null);
        _sut.OnBatchDisposed();

        if (secondWriteBytes > 0)
        {
            var store2 = (WriteBufferAdjuster.CountingWriteBatch)_sut.Wrap(_batch, FlatDbColumns.Account, WriteFlags.None);
            store2.Set(new byte[secondWriteBytes], null);
            _sut.OnBatchDisposed();
        }

        _columnDb.Received(expectedSetWriteBufferCallCount).SetWriteBuffer(Arg.Any<long>());
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
