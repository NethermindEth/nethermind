// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstPackedArrayVariableValueTests
{
    private const int KeySize = 16;

    private static byte[] BuildHsst(byte[][] keys, byte[][] values,
        int strideBytes = HsstPackedArrayVariableValueBuilder<PooledByteBufferWriter.Writer>.DefaultBinaryIndexStrideBytes,
        bool useHashIndex = true)
    {
        using PooledByteBufferWriter pooled = new(16 * 1024 * 1024);
        HsstPackedArrayVariableValueBuilder<PooledByteBufferWriter.Writer> builder = new(
            ref pooled.GetWriter(),
            keySize: KeySize,
            binaryIndexStrideBytes: strideBytes,
            expectedKeyCount: keys.Length,
            useHashIndex: useHashIndex);
        try
        {
            for (int i = 0; i < keys.Length; i++) builder.Add(keys[i], values[i]);
            builder.Build();
            return pooled.WrittenSpan.ToArray();
        }
        finally
        {
            builder.Dispose();
        }
    }

    private static bool TryGet(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = data.Slice((int)b.Offset, b.Length).ToArray();
        return true;
    }

    private static bool TryGetFloor(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeekFloor(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = data.Slice((int)b.Offset, b.Length).ToArray();
        return true;
    }

    private static List<(byte[] Key, byte[] Value)> MaterializeViaEnumerator(ReadOnlySpan<byte> data)
    {
        List<(byte[], byte[])> entries = [];
        SpanByteReader reader = new(data);
        using HsstEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length));
        while (e.MoveNext())
        {
            Bound kb = e.Current.KeyBound;
            Bound vb = e.Current.ValueBound;
            entries.Add((data.Slice((int)kb.Offset, kb.Length).ToArray(),
                         data.Slice((int)vb.Offset, vb.Length).ToArray()));
        }
        return entries;
    }

    private static List<(byte[] Key, byte[] Value)> MaterializeViaMerge(byte[] data)
    {
        List<(byte[], byte[])> entries = [];
        using HsstMergeEnumerator m = new(data);
        while (m.MoveNext(data))
        {
            Bound kb = m.CurrentKey;
            Bound vb = m.CurrentValue;
            entries.Add((data.AsSpan((int)kb.Offset, kb.Length).ToArray(),
                         data.AsSpan((int)vb.Offset, vb.Length).ToArray()));
        }
        return entries;
    }

    private static (byte[][] Keys, byte[][] Values) MakeSortedKeysVariableValues(int count, int seed = 1, int maxValueLen = 64)
    {
        Random rng = new(seed);
        HashSet<string> seen = [];
        List<byte[]> ks = new(count);
        while (ks.Count < count)
        {
            byte[] k = new byte[KeySize];
            rng.NextBytes(k);
            if (seen.Add(Convert.ToHexString(k))) ks.Add(k);
        }
        ks.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        byte[][] vs = ks.Select((_, i) =>
        {
            int len = rng.Next(0, maxValueLen + 1);
            byte[] v = new byte[len];
            rng.NextBytes(v);
            return v;
        }).ToArray();
        return (ks.ToArray(), vs);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(7)]
    [TestCase(256)]
    [TestCase(5000)]
    public void RoundTrip_ExactLookupForEveryKey(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeysVariableValues(count);
        byte[] data = BuildHsst(keys, values);
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.PackedArrayVariableValue));

        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True, $"missing key {i}");
            Assert.That(got, Is.EqualTo(values[i]));
        }

        Random rng = new(99);
        for (int t = 0; t < 64; t++)
        {
            byte[] missing = new byte[KeySize];
            rng.NextBytes(missing);
            if (Array.BinarySearch(keys, missing, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))) >= 0) continue;
            Assert.That(TryGet(data, missing, out _), Is.False);
        }
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(256)]
    [TestCase(5000)]
    public void Floor_AgreesWithLinearSearch(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeysVariableValues(count, seed: 5);
        byte[] data = BuildHsst(keys, values);

        Random rng = new(11);
        for (int t = 0; t < 64; t++)
        {
            byte[] probe = new byte[KeySize];
            rng.NextBytes(probe);

            int floorIdx = -1;
            for (int i = 0; i < count; i++)
            {
                if (keys[i].AsSpan().SequenceCompareTo(probe) <= 0) floorIdx = i; else break;
            }

            bool ok = TryGetFloor(data, probe, out byte[] got);
            if (floorIdx < 0) Assert.That(ok, Is.False);
            else
            {
                Assert.That(ok, Is.True);
                Assert.That(got, Is.EqualTo(values[floorIdx]));
            }
        }
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(256)]
    [TestCase(5000)]
    public void Enumerator_YieldsEntriesInOrder(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeysVariableValues(count, seed: 42);
        byte[] data = BuildHsst(keys, values);

        List<(byte[] K, byte[] V)> seen = MaterializeViaEnumerator(data);
        Assert.That(seen.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(seen[i].K, Is.EqualTo(keys[i]));
            Assert.That(seen[i].V, Is.EqualTo(values[i]));
        }
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(256)]
    [TestCase(5000)]
    public void MergeEnumerator_YieldsEntriesInOrder(int count)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeysVariableValues(count, seed: 77);
        byte[] data = BuildHsst(keys, values);

        List<(byte[] K, byte[] V)> seen = MaterializeViaMerge(data);
        Assert.That(seen.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(seen[i].K, Is.EqualTo(keys[i]));
            Assert.That(seen[i].V, Is.EqualTo(values[i]));
        }
    }

    [TestCase(1, false)]
    [TestCase(7, false)]
    [TestCase(256, false)]
    [TestCase(5000, false)]
    public void NoHashIndex_HitsFloorAndMisses(int count, bool _)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeysVariableValues(count, seed: 23);
        byte[] data = BuildHsst(keys, values, useHashIndex: false);

        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(values[i]));
        }

        // Floor agreement on a few probes.
        Random rng = new(13);
        for (int t = 0; t < 16; t++)
        {
            byte[] probe = new byte[KeySize];
            rng.NextBytes(probe);
            int floorIdx = -1;
            for (int i = 0; i < count; i++)
                if (keys[i].AsSpan().SequenceCompareTo(probe) <= 0) floorIdx = i; else break;
            bool ok = TryGetFloor(data, probe, out byte[] got);
            if (floorIdx < 0) Assert.That(ok, Is.False);
            else { Assert.That(ok, Is.True); Assert.That(got, Is.EqualTo(values[floorIdx])); }
        }
    }

    [Test]
    public void ZeroLengthValues_RoundTrip()
    {
        int count = 32;
        Random rng = new(7);
        HashSet<string> seen = [];
        List<byte[]> ks = new(count);
        while (ks.Count < count)
        {
            byte[] k = new byte[KeySize];
            rng.NextBytes(k);
            if (seen.Add(Convert.ToHexString(k))) ks.Add(k);
        }
        ks.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        byte[][] keys = ks.ToArray();
        byte[][] values = keys.Select(_ => Array.Empty<byte>()).ToArray();

        byte[] data = BuildHsst(keys, values);
        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True);
            Assert.That(got.Length, Is.EqualTo(0));
        }

        // Enumerator agrees.
        List<(byte[] K, byte[] V)> seenE = MaterializeViaEnumerator(data);
        Assert.That(seenE.Count, Is.EqualTo(count));
    }

    [Test]
    public void LargeValues_RoundTrip()
    {
        // Simulate inner-HSST-sized values: a handful of ~256 KiB values.
        int count = 8;
        Random rng = new(101);
        byte[][] ks = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            byte[] k = new byte[KeySize];
            BinaryPrimitives.WriteInt64BigEndian(k, i);
            ks[i] = k;
        }
        byte[][] vs = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            byte[] v = new byte[256 * 1024 + i];
            rng.NextBytes(v);
            vs[i] = v;
        }
        byte[] data = BuildHsst(ks, vs);
        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(data, ks[i], out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(vs[i]));
        }
    }

    [Test]
    public void Empty_HsstReturnsFalse()
    {
        byte[] data = BuildHsst([], []);
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.PackedArrayVariableValue));
        byte[] anyKey = new byte[KeySize];
        Assert.That(TryGet(data, anyKey, out _), Is.False);
        Assert.That(TryGetFloor(data, anyKey, out _), Is.False);
        // Enumerator yields nothing.
        Assert.That(MaterializeViaEnumerator(data).Count, Is.EqualTo(0));
    }

    [Test]
    public void Add_RejectsMismatchedKeyLength()
    {
        using PooledByteBufferWriter pooled = new(1024);
        HsstPackedArrayVariableValueBuilder<PooledByteBufferWriter.Writer> builder =
            new(ref pooled.GetWriter(), KeySize);
        try
        {
            byte[] shortKey = new byte[KeySize - 1];
            byte[] value = [1, 2, 3];
            bool threw = false;
            try { builder.Add(shortKey, value); } catch (ArgumentException) { threw = true; }
            Assert.That(threw, Is.True);
        }
        finally
        {
            builder.Dispose();
        }
    }

    [Test]
    public void Add_RejectsOutOfOrderKeys()
    {
        using PooledByteBufferWriter pooled = new(1024);
        HsstPackedArrayVariableValueBuilder<PooledByteBufferWriter.Writer> builder =
            new(ref pooled.GetWriter(), KeySize);
        try
        {
            byte[] k1 = new byte[KeySize]; k1[0] = 1;
            byte[] k2 = new byte[KeySize]; k2[0] = 2;
            byte[] v = [9, 9];
            builder.Add(k2, v);
            bool threw = false;
            try { builder.Add(k1, v); } catch (InvalidOperationException) { threw = true; }
            Assert.That(threw, Is.True);
        }
        finally
        {
            builder.Dispose();
        }
    }

    [Test]
    public void NoderefEquivalence_MetadataStartResolvesValue()
    {
        // Build the same corpus with PackedArrayVariableValue and confirm that the
        // MetadataStart anchors decoded forward (LEB128 valueLen, KeyLength, key)
        // resolve to the original (key, value) pairs — i.e. interchangeable with
        // any noderef consumer that takes a MetadataStart pointer.
        (byte[][] keys, byte[][] values) = MakeSortedKeysVariableValues(64, seed: 555);
        byte[] data = BuildHsst(keys, values);

        // Walk via merge enumerator; CurrentMetadataStart is the noderef anchor.
        using HsstMergeEnumerator m = new(data);
        int idx = 0;
        while (m.MoveNext(data))
        {
            int metaStart = m.CurrentMetadataStart;
            // Forward-decode from the anchor as a noderef consumer would:
            int pos = metaStart;
            int valueLen = Nethermind.Core.Utils.Leb128.Read(data, ref pos);
            int keyLen = data[pos++];
            Assert.That(keyLen, Is.EqualTo(KeySize));
            byte[] decodedKey = data.AsSpan(pos, keyLen).ToArray();
            byte[] decodedValue = data.AsSpan(metaStart - valueLen, valueLen).ToArray();
            Assert.That(decodedKey, Is.EqualTo(keys[idx]));
            Assert.That(decodedValue, Is.EqualTo(values[idx]));
            idx++;
        }
        Assert.That(idx, Is.EqualTo(keys.Length));
    }
}
