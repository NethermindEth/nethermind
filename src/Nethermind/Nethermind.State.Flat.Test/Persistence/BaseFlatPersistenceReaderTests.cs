// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class BaseFlatPersistenceReaderTests
{
    // Regression: a slot value longer than BaseFlatPersistence.StorageValueSize must fail loudly instead of underflowing
    // the unchecked Unsafe.InitBlockUnaligned in TryGetStorage (which produced a wild memset / SIGSEGV).
    // Shorter values are right-aligned into the 32-byte slot with leading zeros.
    // Cases use rlpWrapSlots:false (the corrupted-DB path). There is no rlpWrapSlots:true throwing case: the
    // value is read into a RlpSlotValueBufferSize (33) byte buffer, so the decoded length cannot exceed 32 —
    // see TryGetStorage_RlpWrapped_DecodesToSlotValue for the wrapped golden path.
    [TestCase(33, true)]
    [TestCase(32, false)]
    [TestCase(16, false)]
    [TestCase(1, false)]
    public void TryGetStorage_RejectsOverLengthValue_ElseRightAligns(int valueLength, bool shouldThrow)
    {
        byte[] value = new byte[valueLength];
        for (int i = 0; i < valueLength; i++) value[i] = (byte)(i + 1);

        FixedValueStore store = new(value);
        BaseFlatPersistence.Reader reader = new(store, store, isPreimageMode: false, rlpWrapSlots: false);

        if (shouldThrow)
        {
            Assert.Throws<InvalidConfigurationException>(() =>
            {
                UInt256 outValue = default;
                reader.TryGetStorage(default, default, ref outValue);
            });
            return;
        }

        UInt256 result = default;
        bool found = reader.TryGetStorage(default, default, ref result);

        byte[] expected = new byte[BaseFlatPersistence.StorageValueSize];
        value.CopyTo(expected, BaseFlatPersistence.StorageValueSize - valueLength);

        Assert.That(found, Is.True);
        Assert.That(result.ToBigEndian(), Is.EqualTo(expected));
    }

    // Golden path: a correctly RLP-wrapped 32-byte value (0xa0 + 32 = 33 bytes on disk) decodes cleanly with
    // rlpWrapSlots:true and must not trip the over-length guard (which checks the decoded length, not 33).
    [Test]
    public void TryGetStorage_RlpWrapped_DecodesToSlotValue()
    {
        byte[] payload = new byte[BaseFlatPersistence.StorageValueSize];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i + 1);
        byte[] rlp = Rlp.Encode(payload).Bytes; // 0xa0 + 32 bytes

        FixedValueStore store = new(rlp);
        BaseFlatPersistence.Reader reader = new(store, store, isPreimageMode: false, rlpWrapSlots: true);

        UInt256 result = default;
        bool found = reader.TryGetStorage(default, default, ref result);

        Assert.That(found, Is.True);
        Assert.That(result.ToBigEndian(), Is.EqualTo(payload));
    }

    // A prefetch must warm exactly the keys the scalar reads ask for, in both storage key layouts and across the
    // 64-key batch boundary; any other key leaves the reads cold.
    [Test]
    public void Prefetch_SendsTheKeysTheScalarReadsUse([Values] bool fullAddressStorageKey)
    {
        const int count = 70;
        RecordingStore accountStore = new();
        RecordingStore storageStore = new();
        BasePersistence.ToHashedFlatReader<BaseFlatPersistence.Reader> reader = new(
            new BaseFlatPersistence.Reader(accountStore, storageStore, fullAddressStorageKey: fullAddressStorageKey));
        Address[] accounts = new Address[count];
        StorageCell[] slots = new StorageCell[count];
        for (int i = 0; i < count; i++)
        {
            accounts[i] = Address.FromNumber((UInt256)(i + 1));
            slots[i] = new StorageCell(accounts[i % 3], (UInt256)(i * 7919));
        }

        reader.Prefetch(accounts, slots);
        UInt256 value = default;
        foreach (Address address in accounts) reader.GetAccount(address);
        foreach (StorageCell slot in slots) reader.TryGetSlot(slot.Address, slot.Index, ref value);

        Assert.That(accountStore.PrefetchBatches, Is.EqualTo(new[] { 64, 6 }));
        Assert.That(storageStore.PrefetchBatches, Is.EqualTo(new[] { 64, 6 }));
        Assert.That(accountStore.PrefetchedKeys, Is.EqualTo(accountStore.ReadKeys));
        Assert.That(storageStore.PrefetchedKeys, Is.EqualTo(storageStore.ReadKeys));
    }

    /// <summary>Records the keys of scalar reads and prefetches; every read misses.</summary>
    private sealed class RecordingStore : ISortedKeyValueStore, IReadPrefetcher
    {
        public List<string> ReadKeys { get; } = [];
        public List<string> PrefetchedKeys { get; } = [];
        public List<int> PrefetchBatches { get; } = [];

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            ReadKeys.Add(Convert.ToHexString(key));
            return null;
        }

        public int Get(scoped ReadOnlySpan<byte> key, Span<byte> output, ReadFlags flags = ReadFlags.None)
        {
            ReadKeys.Add(Convert.ToHexString(key));
            return 0;
        }

        public bool ShouldPrefetch() => true;

        public void Prefetch(ReadOnlySpan<byte> keys, int keyLength)
        {
            PrefetchBatches.Add(keys.Length / keyLength);
            for (int offset = 0; offset < keys.Length; offset += keyLength)
                PrefetchedKeys.Add(Convert.ToHexString(keys.Slice(offset, keyLength)));
        }

        public byte[]? FirstKey => null;
        public byte[]? LastKey => null;
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            throw new NotSupportedException();
    }

    /// <summary>Returns the same value for any key; enough to exercise <c>TryGetStorage</c>'s decode path.</summary>
    private sealed class FixedValueStore(byte[] value) : ISortedKeyValueStore
    {
        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => value;
        public byte[]? FirstKey => null;
        public byte[]? LastKey => null;
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            throw new NotSupportedException();
    }
}
