// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class BaseFlatPersistenceReaderTests
{
    [Test]
    public void MultiGet_DefaultImplementationPreservesInputOrder()
    {
        IReadOnlyKeyValueStore store = new SelectorStore();
        byte[]?[] values = new byte[]?[3];

        store.MultiGet([[3], [1], [2]], values);

        Assert.That(values, Is.EqualTo(new byte[]?[] { [0x33], [0x11], [0x22] }));
    }

    [Test]
    public void TrieReader_BatchedStateRlp_RoutesColumnsAndPreservesOrder()
    {
        RecordingBatchStore stateTop = new(0x10);
        RecordingBatchStore state = new(0x20);
        RecordingBatchStore fallback = new(0x30, missingIndex: 0);
        BaseTriePersistence.Reader reader = new(stateTop, state, new RecordingBatchStore(0x40), fallback);
        IBatchedTrieReader batched = reader;
        TreePath[] paths =
        [
            TreePath.Empty,
            TreePath.FromHexString("01234"),
            TreePath.FromHexString("012345"),
            TreePath.FromHexString("0123456789abcde"),
            TreePath.FromHexString("0123456789abcdef"),
        ];
        byte[]?[] values = new byte[]?[paths.Length];

        batched.TryLoadStateRlpBatch(paths, values, ReadFlags.HintCacheMiss);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateTop.MultiGetCalls, Is.EqualTo(1));
            Assert.That(state.MultiGetCalls, Is.EqualTo(1));
            Assert.That(fallback.MultiGetCalls, Is.EqualTo(1));
            Assert.That(stateTop.Keys![0], Has.Length.EqualTo(3));
            Assert.That(state.Keys![0], Has.Length.EqualTo(8));
            Assert.That(fallback.Keys![0], Has.Length.EqualTo(34));
            Assert.That(fallback.Keys[0][0], Is.EqualTo(0));
            Assert.That(values[0]![0], Is.EqualTo(0x10));
            Assert.That(values[1]![0], Is.EqualTo(0x10));
            Assert.That(values[2]![0], Is.EqualTo(0x20));
            Assert.That(values[3]![0], Is.EqualTo(0x20));
            Assert.That(values[4], Is.Null);
            Assert.That(stateTop.Flags, Is.EqualTo(ReadFlags.HintCacheMiss));
            Assert.That(state.Flags, Is.EqualTo(ReadFlags.HintCacheMiss));
            Assert.That(fallback.Flags, Is.EqualTo(ReadFlags.HintCacheMiss));
        }
    }

    [Test]
    public void TrieReader_BatchedStorageRlp_RoutesColumnsAndPreservesMissingValues()
    {
        RecordingBatchStore storage = new(0x50, missingIndex: 1);
        RecordingBatchStore fallback = new(0x60);
        BaseTriePersistence.Reader reader = new(new RecordingBatchStore(0x10), new RecordingBatchStore(0x20), storage, fallback);
        IBatchedTrieReader batched = reader;
        TreePath[] paths =
        [
            TreePath.Empty,
            TreePath.FromHexString("0123456789abcde"),
            TreePath.FromHexString("0123456789abcdef"),
        ];
        byte[]?[] values = new byte[]?[paths.Length];
        Hash256 address = Keccak.Compute("batch address");

        batched.TryLoadStorageRlpBatch(address, paths, values, ReadFlags.HintReadAhead);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.MultiGetCalls, Is.EqualTo(1));
            Assert.That(fallback.MultiGetCalls, Is.EqualTo(1));
            Assert.That(storage.Keys![0], Has.Length.EqualTo(28));
            Assert.That(fallback.Keys![0], Has.Length.EqualTo(54));
            Assert.That(fallback.Keys[0][0], Is.EqualTo(1));
            Assert.That(values[0]![0], Is.EqualTo(0x50));
            Assert.That(values[1], Is.Null);
            Assert.That(values[2]![0], Is.EqualTo(0x60));
            Assert.That(storage.Flags, Is.EqualTo(ReadFlags.HintReadAhead));
            Assert.That(fallback.Flags, Is.EqualTo(ReadFlags.HintReadAhead));
        }
    }

    [Test]
    public void GetStorage_UsesSingleMultiGetAndPreservesMissingValues()
    {
        byte[] firstAddressBytes = new byte[ValueHash256.MemorySize];
        byte[] secondAddressBytes = new byte[ValueHash256.MemorySize];
        byte[] firstSlotBytes = new byte[ValueHash256.MemorySize];
        byte[] secondSlotBytes = new byte[ValueHash256.MemorySize];
        firstAddressBytes[0] = 0x11;
        secondAddressBytes[0] = 0x22;
        firstSlotBytes[0] = 0x33;
        secondSlotBytes[0] = 0x44;

        TrackingMultiGetStore store = new([[0x12, 0x34], null]);
        BaseFlatPersistence.Reader reader = new(store, store);
        SlotValue?[] values = new SlotValue?[2];

        reader.GetStorage(
            [new ValueHash256(firstAddressBytes), new ValueHash256(secondAddressBytes)],
            [new ValueHash256(firstSlotBytes), new ValueHash256(secondSlotBytes)],
            values);

        Assert.That(store.MultiGetCalls, Is.EqualTo(1));
        Assert.That(store.Flags, Is.EqualTo(ReadFlags.HintCacheMiss));
        Assert.That(store.Keys, Has.Length.EqualTo(2));
        Assert.That(store.Keys![0], Has.Length.EqualTo(52));
        Assert.That(store.Keys[0][0], Is.EqualTo(0x11));
        Assert.That(store.Keys[0][4], Is.EqualTo(0x33));
        Assert.That(values[0]!.Value.ToEvmBytes(), Is.EqualTo(new byte[] { 0x12, 0x34 }));
        Assert.That(values[1], Is.Null);
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
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            throw new NotSupportedException();
    }

    private sealed class TrackingMultiGetStore(byte[]?[] results) : ISortedKeyValueStore
    {
        public int MultiGetCalls { get; private set; }
        public byte[][]? Keys { get; private set; }
        public ReadFlags Flags { get; private set; }

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
            throw new AssertionException("Point reads are not expected.");

        public void MultiGet(byte[][] keys, Span<byte[]?> values, ReadFlags flags = ReadFlags.None)
        {
            MultiGetCalls++;
            Keys = keys;
            Flags = flags;
            results.CopyTo(values);
        }

        public byte[]? FirstKey => null;
        public byte[]? LastKey => null;
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingBatchStore(byte marker, int missingIndex = -1) : ISortedKeyValueStore
    {
        public int MultiGetCalls { get; private set; }
        public byte[][]? Keys { get; private set; }
        public ReadFlags Flags { get; private set; }

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
            throw new AssertionException("Point reads are not expected.");

        public void MultiGet(byte[][] keys, Span<byte[]?> values, ReadFlags flags = ReadFlags.None)
        {
            MultiGetCalls++;
            Keys = keys;
            Flags = flags;
            for (int i = 0; i < values.Length; i++)
                values[i] = i == missingIndex ? null : [marker, (byte)keys[i].Length];
        }

        public byte[]? FirstKey => null;
        public byte[]? LastKey => null;
        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            throw new NotSupportedException();
    }

    private sealed class SelectorStore : IReadOnlyKeyValueStore
    {
        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => [(byte)(key[0] * 0x11)];
    }
}
