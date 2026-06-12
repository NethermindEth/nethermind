// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class CachedReaderPersistenceTests
{
    private CancellationTokenSource _processExit = null!;

    [SetUp]
    public void SetUp() => _processExit = new CancellationTokenSource();

    [TearDown]
    public void TearDown() => _processExit.Dispose();

    [Test]
    public async Task Repeated_storage_reads_use_cached_reader_snapshot()
    {
        CountingReader innerReader = new(SlotValue.FromSpanWithoutLeadingZero([0x01]));
        FakePersistence persistence = new(innerReader);
        await using CachedReaderPersistence cachedPersistence = CreateCachedPersistence(persistence);

        using IPersistence.IPersistenceReader reader = cachedPersistence.CreateReader();
        SlotValue first = default;
        SlotValue second = default;

        Assert.That(reader.TryGetSlot(TestItem.AddressA, 1, ref first), Is.True);
        Assert.That(reader.TryGetSlot(TestItem.AddressA, 1, ref second), Is.True);

        Assert.That(innerReader.StorageReads, Is.EqualTo(1));
        AssertSlotValue(first, 0x01);
        AssertSlotValue(second, 0x01);
    }

    [Test]
    public async Task Write_batch_dispose_recreates_reader_snapshot_for_uncached_slots()
    {
        CountingReader firstReader = new(SlotValue.FromSpanWithoutLeadingZero([0x01]));
        FakePersistence persistence = new(firstReader);
        await using CachedReaderPersistence cachedPersistence = CreateCachedPersistence(persistence);

        using (IPersistence.IPersistenceReader reader = cachedPersistence.CreateReader())
        {
            SlotValue value = default;
            Assert.That(reader.TryGetSlot(TestItem.AddressA, 1, ref value), Is.True);
            AssertSlotValue(value, 0x01);
        }

        CountingReader secondReader = new(SlotValue.FromSpanWithoutLeadingZero([0x02]));
        persistence.Reader = secondReader;
        using (cachedPersistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256)))
        {
        }

        using (IPersistence.IPersistenceReader reader = cachedPersistence.CreateReader())
        {
            SlotValue value = default;
            Assert.That(reader.TryGetSlot(TestItem.AddressA, 2, ref value), Is.True);
            AssertSlotValue(value, 0x02);
        }

        Assert.That(firstReader.StorageReads, Is.EqualTo(1));
        Assert.That(secondReader.StorageReads, Is.EqualTo(1));
    }

    [Test]
    public async Task Committed_storage_write_updates_shared_slot_cache()
    {
        CountingReader firstReader = new(SlotValue.FromSpanWithoutLeadingZero([0x01]));
        FakePersistence persistence = new(firstReader);
        await using CachedReaderPersistence cachedPersistence = CreateCachedPersistence(persistence);

        using (IPersistence.IPersistenceReader reader = cachedPersistence.CreateReader())
        {
            SlotValue value = default;
            Assert.That(reader.TryGetSlot(TestItem.AddressA, 1, ref value), Is.True);
            AssertSlotValue(value, 0x01);
        }

        CountingReader secondReader = new(SlotValue.FromSpanWithoutLeadingZero([0x03]));
        persistence.Reader = secondReader;
        using (IPersistence.IWriteBatch writeBatch = cachedPersistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256)))
        {
            SlotValue? updatedValue = SlotValue.FromSpanWithoutLeadingZero([0x02]);
            writeBatch.SetStorage(TestItem.AddressA, 1, updatedValue);
        }

        using (IPersistence.IPersistenceReader reader = cachedPersistence.CreateReader())
        {
            SlotValue value = default;
            Assert.That(reader.TryGetSlot(TestItem.AddressA, 1, ref value), Is.True);
            AssertSlotValue(value, 0x02);
        }

        Assert.That(firstReader.StorageReads, Is.EqualTo(1));
        Assert.That(secondReader.StorageReads, Is.Zero);
    }

    [Test]
    public async Task Committed_storage_delete_updates_shared_slot_cache()
    {
        CountingReader firstReader = new(SlotValue.FromSpanWithoutLeadingZero([0x01]));
        FakePersistence persistence = new(firstReader);
        await using CachedReaderPersistence cachedPersistence = CreateCachedPersistence(persistence);

        using (IPersistence.IPersistenceReader reader = cachedPersistence.CreateReader())
        {
            SlotValue value = default;
            Assert.That(reader.TryGetSlot(TestItem.AddressA, 1, ref value), Is.True);
            AssertSlotValue(value, 0x01);
        }

        CountingReader secondReader = new(SlotValue.FromSpanWithoutLeadingZero([0x02]));
        persistence.Reader = secondReader;
        using (IPersistence.IWriteBatch writeBatch = cachedPersistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256)))
        {
            SlotValue? deletedValue = null;
            writeBatch.SetStorage(TestItem.AddressA, 1, deletedValue);
        }

        using (IPersistence.IPersistenceReader reader = cachedPersistence.CreateReader())
        {
            SlotValue value = default;
            Assert.That(reader.TryGetSlot(TestItem.AddressA, 1, ref value), Is.False);
        }

        Assert.That(firstReader.StorageReads, Is.EqualTo(1));
        Assert.That(secondReader.StorageReads, Is.Zero);
    }

    [Test]
    public async Task Raw_storage_write_clears_shared_slot_cache()
    {
        CountingReader firstReader = new(SlotValue.FromSpanWithoutLeadingZero([0x01]));
        FakePersistence persistence = new(firstReader);
        await using CachedReaderPersistence cachedPersistence = CreateCachedPersistence(persistence);

        using (IPersistence.IPersistenceReader reader = cachedPersistence.CreateReader())
        {
            SlotValue value = default;
            Assert.That(reader.TryGetSlot(TestItem.AddressA, 1, ref value), Is.True);
            AssertSlotValue(value, 0x01);
        }

        CountingReader secondReader = new(SlotValue.FromSpanWithoutLeadingZero([0x02]));
        persistence.Reader = secondReader;
        using (IPersistence.IWriteBatch writeBatch = cachedPersistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256)))
        {
            writeBatch.SetStorageRawEncoded(TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256, [0x80]);
        }

        using (IPersistence.IPersistenceReader reader = cachedPersistence.CreateReader())
        {
            SlotValue value = default;
            Assert.That(reader.TryGetSlot(TestItem.AddressA, 1, ref value), Is.True);
            AssertSlotValue(value, 0x02);
        }

        Assert.That(firstReader.StorageReads, Is.EqualTo(1));
        Assert.That(secondReader.StorageReads, Is.EqualTo(1));
    }

    [Test]
    public async Task Broad_storage_mutation_drops_earlier_tracked_storage_updates()
    {
        CountingReader firstReader = new(SlotValue.FromSpanWithoutLeadingZero([0x01]));
        FakePersistence persistence = new(firstReader);
        await using CachedReaderPersistence cachedPersistence = CreateCachedPersistence(persistence);

        using (IPersistence.IPersistenceReader reader = cachedPersistence.CreateReader())
        {
            SlotValue value = default;
            Assert.That(reader.TryGetSlot(TestItem.AddressA, 1, ref value), Is.True);
            AssertSlotValue(value, 0x01);
        }

        CountingReader secondReader = new(SlotValue.FromSpanWithoutLeadingZero([0x03]));
        persistence.Reader = secondReader;
        using (IPersistence.IWriteBatch writeBatch = cachedPersistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256)))
        {
            SlotValue? updatedValue = SlotValue.FromSpanWithoutLeadingZero([0x02]);
            writeBatch.SetStorage(TestItem.AddressA, 1, updatedValue);
            writeBatch.SelfDestruct(TestItem.AddressA);
        }

        using (IPersistence.IPersistenceReader reader = cachedPersistence.CreateReader())
        {
            SlotValue value = default;
            Assert.That(reader.TryGetSlot(TestItem.AddressA, 1, ref value), Is.True);
            AssertSlotValue(value, 0x03);
        }

        Assert.That(firstReader.StorageReads, Is.EqualTo(1));
        Assert.That(secondReader.StorageReads, Is.EqualTo(1));
    }

    [Test]
    public async Task Stale_reader_does_not_overwrite_shared_slot_cache_after_write()
    {
        CountingReader firstReader = new(SlotValue.FromSpanWithoutLeadingZero([0x01]));
        FakePersistence persistence = new(firstReader);
        await using CachedReaderPersistence cachedPersistence = CreateCachedPersistence(persistence);

        using IPersistence.IPersistenceReader staleReader = cachedPersistence.CreateReader();
        SlotValue staleValue = default;
        Assert.That(staleReader.TryGetSlot(TestItem.AddressA, 1, ref staleValue), Is.True);
        AssertSlotValue(staleValue, 0x01);

        CountingReader secondReader = new(SlotValue.FromSpanWithoutLeadingZero([0x03]));
        persistence.Reader = secondReader;
        using (IPersistence.IWriteBatch writeBatch = cachedPersistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256)))
        {
            SlotValue? updatedValue = SlotValue.FromSpanWithoutLeadingZero([0x02]);
            writeBatch.SetStorage(TestItem.AddressA, 1, updatedValue);
        }

        staleValue = default;
        Assert.That(staleReader.TryGetSlot(TestItem.AddressA, 1, ref staleValue), Is.True);
        AssertSlotValue(staleValue, 0x01);

        using (IPersistence.IPersistenceReader currentReader = cachedPersistence.CreateReader())
        {
            SlotValue currentValue = default;
            Assert.That(currentReader.TryGetSlot(TestItem.AddressA, 1, ref currentValue), Is.True);
            AssertSlotValue(currentValue, 0x02);
        }

        Assert.That(firstReader.StorageReads, Is.EqualTo(2));
        Assert.That(secondReader.StorageReads, Is.Zero);
    }

    private CachedReaderPersistence CreateCachedPersistence(IPersistence persistence) =>
        new(persistence, new ProcessExitSource(_processExit.Token), LimboLogs.Instance);

    private static void AssertSlotValue(SlotValue value, byte expected) =>
        Assert.That(value.ToEvmBytes(), Is.EqualTo(new byte[] { expected }));

    private sealed class ProcessExitSource(CancellationToken token) : IProcessExitSource
    {
        public CancellationToken Token { get; } = token;

        public void Exit(int exitCode) { }
    }

    private sealed class FakePersistence(CountingReader reader) : IPersistence
    {
        public CountingReader Reader { get; set; } = reader;

        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => Reader;

        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) =>
            new FakeWriteBatch();

        public void Flush() { }

        public void Clear() { }
    }

    private sealed class CountingReader(SlotValue value) : IPersistence.IPersistenceReader
    {
        public int StorageReads { get; private set; }

        public StateId CurrentState { get; } = StateId.PreGenesis;

        public Account? GetAccount(Address address) => throw new NotSupportedException();

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            StorageReads++;
            outValue = value;
            return true;
        }

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => throw new NotSupportedException();

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => throw new NotSupportedException();

        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => throw new NotSupportedException();

        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) =>
            throw new NotSupportedException();

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
            throw new NotSupportedException();

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            throw new NotSupportedException();

        public bool IsPreimageMode => false;

        public void Dispose() { }
    }
}
