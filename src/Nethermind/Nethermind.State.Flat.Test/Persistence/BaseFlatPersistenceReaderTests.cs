// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

    [TestCase(false, TestName = "BatchSlots_LegacyFlatReader_UsesScalarFallback")]
    [TestCase(true, TestName = "BatchSlots_LegacyHashedReader_UsesScalarFallback")]
    public void BatchSlots_WithoutBatchImplementation_PreservesScalarResults(bool hashed)
    {
        UInt256?[] values = new UInt256?[4];
        if (hashed)
        {
            BasePersistence.IHashedFlatReader reader = new ScalarHashedReader();
            (ValueHash256 Address, ValueHash256 Slot)[] cells =
                [(default, default), (default, ValueKeccak.OfAnEmptyString), (default, ValueKeccak.EmptyTreeHash), (default, default)];
            reader.GetSlots(cells, values);
        }
        else
        {
            BasePersistence.IFlatReader reader = new ScalarReader();
            StorageCell[] cells =
                [new(Address.Zero, UInt256.Zero), new(Address.Zero, UInt256.One), new(Address.Zero, UInt256.MaxValue), new(Address.Zero, UInt256.Zero)];
            reader.GetSlots(cells, values);
        }

        Assert.That(values, Is.EqualTo(new UInt256?[] { UInt256.Zero, null, UInt256.MaxValue, UInt256.Zero }),
            "a scalar-only backend must compile and retain batch order, missing values, zero and all 32 value bytes");
    }

    private readonly struct ScalarReader : BasePersistence.IFlatReader
    {
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
        {
            outValue = slot;
            return slot != UInt256.One;
        }
        public Account? GetAccount(Address address) => throw new NotSupportedException();
        public byte[]? GetAccountRaw(in ValueHash256 address) => throw new NotSupportedException();
        public bool TryGetSlotRaw(in ValueHash256 address, in ValueHash256 slot, ref UInt256 outValue) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
        public bool IsPreimageMode => false;
    }

    private readonly struct ScalarHashedReader : BasePersistence.IHashedFlatReader
    {
        public bool TryGetStorage(in ValueHash256 address, in ValueHash256 slot, ref UInt256 outValue)
        {
            outValue = slot == default ? UInt256.Zero : UInt256.MaxValue;
            return slot != ValueKeccak.OfAnEmptyString;
        }
        public int GetAccount(in ValueHash256 address, Span<byte> buffer) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
        public bool IsPreimageMode => false;
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
