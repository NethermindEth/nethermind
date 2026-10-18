// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Core.Exceptions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class BaseFlatPersistenceReaderTests
{
    [Test]
    public void GetAccounts_WithIndependentKeys_UsesDecodedBatchWithoutScalarReads([Values(8, 64)] int count)
    {
        Account expected = new(7, (UInt256.One << 200) + 19);
        using ArrayPoolSpan<byte> encoded = AccountDecoder.Slim.EncodeToArrayPoolSpan(expected);
        AccountBatchStore store = new(((ReadOnlySpan<byte>)encoded).ToArray());
        BaseFlatPersistence.Reader reader = new(store, store);
        ValueHash256[] paths = new ValueHash256[count];
        for (int i = 0; i < paths.Length; i++) paths[i] = new ValueHash256(Keccak.Compute(i.ToString()).Bytes);
        Account?[] actual = new Account?[count];

        ((BasePersistence.IHashedFlatReader)reader).GetAccounts(paths, actual, AccountDecoder.Slim);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.BatchReads, Is.EqualTo(1), "independent account keys must share one decoded database call");
            Assert.That(store.ScalarReads, Is.Zero, "batch reads must not also perform scalar database reads");
            Assert.That(actual, Is.All.EqualTo(expected), "native slices must decode to complete owned account objects");
        }
    }

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

    /// <summary>Returns the same value for any key; enough to exercise <c>TryGetStorage</c>'s decode path.</summary>
    private sealed class FixedValueStore(byte[] value) : ISortedKeyValueStore
    {
        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => value;
        public byte[]? FirstKey => null;
        public byte[]? LastKey => null;
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            throw new NotSupportedException();
    }
    private sealed class AccountBatchStore(byte[] value) : ISortedKeyValueStore, IBatchReadOnlyDb
    {
        internal int BatchReads { get; private set; }
        internal int ScalarReads { get; private set; }
        public byte[]? FirstKey => null;
        public byte[]? LastKey => null;
        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            ScalarReads++;
            return value;
        }
        public void GetMany<T, TDecoder>(ReadOnlySpan<byte> keys, int keyLength, Span<T> values, TDecoder decoder)
            where TDecoder : IValueDecoder<T>
        {
            Assert.That(keyLength, Is.EqualTo(20), "account reads must use the State column's encoded key width");
            Assert.That(keys.Length, Is.EqualTo(keyLength * values.Length), "every result must have exactly one encoded key");
            BatchReads++;
            for (int i = 0; i < values.Length; i++) values[i] = decoder.Decode(value);
        }
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive,
            ReadFlags flags = ReadFlags.None) => throw new NotSupportedException();
    }

}
