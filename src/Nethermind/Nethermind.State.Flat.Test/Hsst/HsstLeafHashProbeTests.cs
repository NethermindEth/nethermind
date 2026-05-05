// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.State.Flat.BSearchIndex;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstLeafHashProbeTests
{
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

    private static (byte[][] Keys, byte[][] Values) MakeSortedKeys(int count, int seed = 1)
    {
        Random rng = new(seed);
        HashSet<string> seen = [];
        List<byte[]> ks = new(count);
        while (ks.Count < count)
        {
            byte[] k = new byte[16];
            rng.NextBytes(k);
            if (seen.Add(Convert.ToHexString(k))) ks.Add(k);
        }
        ks.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        byte[][] vs = ks.Select((_, i) =>
        {
            byte[] v = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(v, i);
            BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(4), i * 31);
            return v;
        }).ToArray();
        return (ks.ToArray(), vs);
    }

    // Cover the small-leaf, multi-leaf, and probe-cap-fallback cases for both widths;
    // also include the inline-values mode so the probe path through GetValue + KeyBound is exercised.
    [TestCase(HashProbeMode.OneByte, 1, false)]
    [TestCase(HashProbeMode.OneByte, 50, false)]
    [TestCase(HashProbeMode.OneByte, 200, false)]
    [TestCase(HashProbeMode.OneByte, 500, false)]    // forces multi-leaf b-tree
    [TestCase(HashProbeMode.OneByte, 5000, false)]
    [TestCase(HashProbeMode.TwoBytes, 50, false)]
    [TestCase(HashProbeMode.TwoBytes, 500, false)]
    [TestCase(HashProbeMode.TwoBytes, 5000, false)]
    [TestCase(HashProbeMode.OneByte, 50, true)]      // inline
    [TestCase(HashProbeMode.TwoBytes, 200, true)]    // inline
    public void Probe_RoundTrip_MatchesPlainBTree(HashProbeMode mode, int count, bool inlineValues)
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count, seed: 42);

        byte[] withProbe = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        }, leafHashProbeMode: mode, inlineValues: inlineValues);

        byte[] plain = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < count; i++) b.Add(keys[i], values[i]);
        }, inlineValues: inlineValues);

        // Every present key resolves identically.
        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(withProbe, keys[i], out byte[] gotProbe), Is.True, $"probe: missing key {i}");
            Assert.That(gotProbe, Is.EqualTo(values[i]));
            Assert.That(TryGet(plain, keys[i], out byte[] gotPlain), Is.True);
            Assert.That(gotPlain, Is.EqualTo(values[i]));
        }

        // Absent-key probes (exact and floor) match the plain b-tree's answers.
        Random rng = new(99);
        Comparer<byte[]> cmp = Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b));
        int verified = 0;
        for (int t = 0; verified < 32 && t < 256; t++)
        {
            byte[] missing = new byte[16];
            rng.NextBytes(missing);
            if (Array.BinarySearch(keys, missing, cmp) >= 0) continue;
            verified++;

            Assert.That(TryGet(withProbe, missing, out _), Is.False);
            Assert.That(TryGet(plain, missing, out _), Is.False);

            bool fp = TryGetFloor(withProbe, missing, out byte[] fpv);
            bool ff = TryGetFloor(plain, missing, out byte[] ffv);
            Assert.That(fp, Is.EqualTo(ff));
            if (fp) Assert.That(fpv, Is.EqualTo(ffv));
        }
    }

    [Test]
    public void Probe_OneByte_LargeLeaf_FallsBackToNone()
    {
        // OneByte probe caps at <254 entries per leaf. With maxLeafEntries=255 and
        // a single oversized leaf, the writer must skip the probe section entirely
        // (no bit-7 set, no extended flags), and reads must still succeed.
        const int count = 255;
        (byte[][] keys, byte[][] values) = MakeSortedKeys(count, seed: 7);

        // Force a single leaf by allowing 255 entries per leaf.
        using PooledByteBufferWriter pooled = new(10 * 1024 * 1024);
        HsstBuilder<PooledByteBufferWriter.Writer> builder = new(ref pooled.GetWriter(),
            leafHashProbeMode: HashProbeMode.OneByte);
        try
        {
            for (int i = 0; i < count; i++) builder.Add(keys[i], values[i]);
            builder.Build(maxLeafEntries: 255);
        }
        finally
        {
            builder.Dispose();
        }

        byte[] data = pooled.WrittenSpan.ToArray();

        for (int i = 0; i < count; i++)
        {
            Assert.That(TryGet(data, keys[i], out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(values[i]));
        }
    }

    [Test]
    public void Probe_BackwardCompat_PlainNodeUnchanged()
    {
        // A node built without any probe must round-trip identically to a node
        // built with the previous-format writer (no extended flags byte). We
        // verify the trailing IndexType is still 0x01 and the metadata's primary
        // flags byte does not have bit 7 set.
        (byte[][] keys, byte[][] values) = MakeSortedKeys(50, seed: 3);

        byte[] withoutProbe = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < keys.Length; i++) b.Add(keys[i], values[i]);
        });

        Assert.That(withoutProbe[^1], Is.EqualTo((byte)IndexType.BTree));

        // Last metadata length byte sits at index ^2 (just before the IndexType).
        int metadataLen = withoutProbe[^2];
        // Metadata starts at (length - 1 - metadataLen - 1) since IndexType is the very last byte.
        int metadataStart = withoutProbe.Length - 1 - 1 - metadataLen;
        byte flags = withoutProbe[metadataStart];
        Assert.That(flags & 0x80, Is.EqualTo(0), "bit 7 should not be set on plain leaf");
    }

    [Test]
    public void Probe_OneByte_ExtendedFlagsSet()
    {
        (byte[][] keys, byte[][] values) = MakeSortedKeys(50, seed: 11);

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer> b) =>
        {
            for (int i = 0; i < keys.Length; i++) b.Add(keys[i], values[i]);
        }, leafHashProbeMode: HashProbeMode.OneByte);

        int metadataLen = data[^2];
        int metadataStart = data.Length - 1 - 1 - metadataLen;
        byte flags = data[metadataStart];
        byte extFlags = data[metadataStart + 1];
        Assert.That(flags & 0x80, Is.Not.EqualTo(0), "bit 7 must be set when probe present");
        Assert.That(extFlags & 0x01, Is.Not.EqualTo(0), "ext bit 0 must be set for OneByte probe");
        Assert.That(extFlags & 0x02, Is.EqualTo(0), "ext bit 1 must NOT be set for OneByte probe");
    }
}
