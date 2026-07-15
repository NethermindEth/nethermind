// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class BaseFlatPersistenceReaderTests
{
    [Test]
    public void GetAccounts_UsesSingleMultiGetWithEncodedKeys()
    {
        ValueHash256 first = new(Enumerable.Range(0, ValueHash256.MemorySize).Select(static i => (byte)i).ToArray());
        ValueHash256 second = new(Enumerable.Range(0, ValueHash256.MemorySize).Select(static i => (byte)(255 - i)).ToArray());
        TrackingMultiGetStore store = new();
        BaseFlatPersistence.Reader reader = new(store, store);
        byte[]?[] accounts = new byte[]?[2];

        reader.GetAccounts([first, second], accounts);

        Assert.That(store.MultiGetCalls, Is.EqualTo(1));
        Assert.That(store.Keys, Has.Length.EqualTo(2));
        Assert.That(store.Keys![0], Is.EqualTo(first.Bytes[..20].ToArray()));
        Assert.That(store.Keys[1], Is.EqualTo(second.Bytes[..20].ToArray()));
        Assert.That(accounts, Is.EqualTo(new byte[]?[] { [1], [2] }));
    }

    // Regression: a slot value longer than SlotValue.ByteCount must fail loudly instead of underflowing
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
                SlotValue outValue = default;
                reader.TryGetStorage(default, default, ref outValue);
            });
            return;
        }

        SlotValue result = default;
        bool found = reader.TryGetStorage(default, default, ref result);

        byte[] expected = new byte[SlotValue.ByteCount];
        value.CopyTo(expected, SlotValue.ByteCount - valueLength);

        Assert.That(found, Is.True);
        Assert.That(result.AsReadOnlySpan.ToArray(), Is.EqualTo(expected));
    }

    // Golden path: a correctly RLP-wrapped 32-byte value (0xa0 + 32 = 33 bytes on disk) decodes cleanly with
    // rlpWrapSlots:true and must not trip the over-length guard (which checks the decoded length, not 33).
    [Test]
    public void TryGetStorage_RlpWrapped_DecodesToSlotValue()
    {
        byte[] payload = new byte[SlotValue.ByteCount];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i + 1);
        byte[] rlp = Rlp.Encode(payload).Bytes; // 0xa0 + 32 bytes

        FixedValueStore store = new(rlp);
        BaseFlatPersistence.Reader reader = new(store, store, isPreimageMode: false, rlpWrapSlots: true);

        SlotValue result = default;
        bool found = reader.TryGetStorage(default, default, ref result);

        Assert.That(found, Is.True);
        Assert.That(result.AsReadOnlySpan.ToArray(), Is.EqualTo(payload));
    }

    /// <summary>Returns the same value for any key; enough to exercise <c>TryGetStorage</c>'s decode path.</summary>
    private sealed class FixedValueStore(byte[] value) : ISortedKeyValueStore
    {
        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => value;
        public byte[]? FirstKey => null;
        public byte[]? LastKey => null;
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) =>
            throw new NotSupportedException();
    }

    private sealed class TrackingMultiGetStore : ISortedKeyValueStore
    {
        public int MultiGetCalls { get; private set; }
        public byte[][]? Keys { get; private set; }

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
            throw new AssertionException("Point reads are not expected.");

        public void MultiGet(byte[][] keys, Span<byte[]?> values, ReadFlags flags = ReadFlags.None)
        {
            MultiGetCalls++;
            Keys = keys;
            for (int i = 0; i < values.Length; i++)
                values[i] = [(byte)(i + 1)];
        }

        public byte[]? FirstKey => null;
        public byte[]? LastKey => null;
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) =>
            throw new NotSupportedException();
    }
}
