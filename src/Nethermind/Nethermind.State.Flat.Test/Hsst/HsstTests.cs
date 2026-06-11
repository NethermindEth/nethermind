// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;
using Nethermind.State.Flat.Hsst.BTree;

namespace Nethermind.State.Flat.Test.Hsst;

[TestFixture]
public class HsstTests
{
    // ----- Helpers wrapping HsstReader/HsstRefEnumerator so the original test
    //       bodies stay close to their pre-migration shape.

    /// <summary>Exact-match lookup. Returns false when <paramref name="key"/> isn't present.</summary>
    private static bool TryGet(ReadOnlySpan<byte> data, scoped ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        if (!r.TrySeek(key, out _)) { value = []; return false; }
        Bound b = r.GetBound();
        value = data.Slice((int)b.Offset, (int)b.Length).ToArray();
        return true;
    }

    /// <summary>Walk the HSST and materialise every (key, value) pair as byte arrays.</summary>
    private static List<(byte[] Key, byte[] Value)> Materialize(ReadOnlySpan<byte> data)
    {
        List<(byte[] Key, byte[] Value)> entries = [];
        SpanByteReader reader = new(data);
        using HsstRefEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length));
        Span<byte> keyBuf = stackalloc byte[256];
        while (e.MoveNext())
        {
            byte[] k = e.CopyCurrentLogicalKey(keyBuf).ToArray();
            Bound vb = e.Current.ValueBound;
            byte[] v = data.Slice((int)vb.Offset, (int)vb.Length).ToArray();
            entries.Add((k, v));
        }
        return entries;
    }

    private static int CountEntries(ReadOnlySpan<byte> data) => Materialize(data).Count;

    [TestCase(0L, 1)]
    [TestCase(1L, 1)]
    [TestCase(127L, 1)]
    [TestCase(128L, 2)]
    [TestCase(255L, 2)]
    [TestCase(16383L, 2)]
    [TestCase(16384L, 3)]
    [TestCase((long)int.MaxValue, 5)]
    [TestCase((long)int.MaxValue + 1, 5)]
    [TestCase(1L << 35, 6)]
    // long.MaxValue is 63 bits (top bit clear), so it encodes in ⌈63/7⌉=9 bytes.
    // The 10-byte worst case is only reached when the 64th bit is set, e.g. -1L
    // (whose ulong reinterpretation is all-ones).
    [TestCase(long.MaxValue, 9)]
    [TestCase(-1L, 10)]
    public void Leb128_RoundTrip(long value, int expectedSize)
    {
        Assert.That(Leb128.EncodedSize(value), Is.EqualTo(expectedSize));

        byte[] buffer = new byte[16];
        int endPos = Leb128.Write(buffer, 0, value);
        Assert.That(endPos, Is.EqualTo(expectedSize));

        int readPos = 0;
        long decoded = Leb128.Read(buffer, ref readPos);
        Assert.That(decoded, Is.EqualTo(value));
        Assert.That(readPos, Is.EqualTo(expectedSize));
    }

    [Test]
    public void Empty_Hsst_HasZeroEntries()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) => { });

        Assert.That(CountEntries(data), Is.EqualTo(0));
        Assert.That(TryGet(data, "hello"u8, out _), Is.False);
    }

    [Test]
    public void IndexType_Byte_Is_BTree_At_Tail()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            builder.Add("key"u8, "value"u8);
        });

        Assert.That(data[^1], Is.EqualTo((byte)IndexType.BTree));
    }

    [Test]
    public void Single_Entry_RoundTrip()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            builder.Add("key1"u8, "value1"u8);
        });

        Assert.That(CountEntries(data), Is.EqualTo(1));

        Assert.That(TryGet(data, "key1"u8, out byte[] val), Is.True);
        Assert.That(Encoding.UTF8.GetString(val), Is.EqualTo("value1"));

        Assert.That(TryGet(data, "key2"u8, out _), Is.False);
        Assert.That(TryGet(data, "key0"u8, out _), Is.False);
    }

    [TestCase(2)]
    [TestCase(10)]
    [TestCase(64)]
    [TestCase(65)]
    [TestCase(128)]
    [TestCase(200)]
    [TestCase(1000)]
    [TestCase(5000)]
    public void Multiple_Entries_RoundTrip(int count)
    {
        List<(string Key, string Value)> expected = [];
        for (int i = 0; i < count; i++)
        {
            string key = $"key_{i:D6}";
            string value = $"val_{i:D6}";
            expected.Add((key, value));
        }

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            foreach ((string key, string value) in expected)
            {
                builder.Add(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
            }
        });

        Assert.That(CountEntries(data), Is.EqualTo(count));

        expected.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

        foreach ((string key, string value) in expected)
        {
            Assert.That(TryGet(data, Encoding.UTF8.GetBytes(key), out byte[] val), Is.True, $"Key {key} not found");
            Assert.That(Encoding.UTF8.GetString(val), Is.EqualTo(value));
        }

        Assert.That(TryGet(data, "zzz_not_exist"u8, out _), Is.False);
        Assert.That(TryGet(data, ""u8, out _), Is.False);
    }

    /// <summary>
    /// Regression test for <see cref="Nethermind.State.Flat.Hsst.HsstEnumerator{TReader, TPin}"/>'s
    /// mixed-kind intermediate handling in <c>DescendToLeaf</c>.
    /// </summary>
    /// <remarks>
    /// Interleaves small entries (16-byte values) with large entries (~6 KiB
    /// values). The large values cross page boundaries during the write, so
    /// the builder's <c>FlushPendingNotOnCurrentPage</c> direct-flushes the
    /// stranded entries as <c>NodeKind=Entry</c> descriptors onto
    /// <c>CurrentLevel</c>. Those interleave with <c>NodeKind=Intermediate</c>
    /// descriptors from <c>EmitInlineLeaf</c> for the small-entry runs;
    /// <c>ChooseIntermediateChildCount</c> packs them without kind awareness,
    /// so the resulting intermediates carry mixed Entry+Intermediate children.
    ///
    /// The enumerator's descent must scan every child's flag byte (not just
    /// the leftmost) before treating a node as leaf-level. If it short-circuits
    /// on the leftmost-is-Entry check alone, <c>BufferLeaf</c> mis-treats
    /// inner-node positions as entry positions and the enumeration truncates.
    /// </remarks>
    [TestCase(20)]
    [TestCase(100)]
    [TestCase(500)]
    public void Enumeration_YieldsAllEntries_With_PageCrossing_Values(int count)
    {
        List<(string Key, byte[] Value)> expected = new(count);
        for (int i = 0; i < count; i++)
        {
            // Every fifth entry has a ~6 KiB value (crosses two 4 KiB pages); the
            // others are small enough to fit alongside their leaf node on the
            // same page. The mix forces the prune + direct-flush path to fire
            // at boundary transitions.
            byte[] value = (i % 5 == 0)
                ? new byte[6 * 1024]
                : new byte[16];
            // Fill values with a deterministic per-entry pattern so a mis-read
            // (e.g. via BufferLeaf on a non-entry position) surfaces as a value
            // mismatch rather than passing silently.
            for (int j = 0; j < value.Length; j++) value[j] = (byte)((i + j) & 0xFF);
            expected.Add(($"key_{i:D6}", value));
        }

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            foreach ((string key, byte[] value) in expected)
            {
                builder.Add(Encoding.UTF8.GetBytes(key), value);
            }
        });

        // Enumerate via HsstRefEnumerator and verify count, ordering, and per-entry value bytes.
        List<(byte[] Key, byte[] Value)> actual = Materialize(data);
        Assert.That(actual.Count, Is.EqualTo(count));

        expected.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        for (int i = 0; i < count; i++)
        {
            Assert.That(Encoding.UTF8.GetString(actual[i].Key), Is.EqualTo(expected[i].Key), $"Key mismatch at index {i}");
            Assert.That(actual[i].Value, Is.EqualTo(expected[i].Value), $"Value mismatch at key {expected[i].Key}");
        }

        // Per-key seek (TrySeek path, independent of the enumerator).
        foreach ((string key, byte[] value) in expected)
        {
            Assert.That(TryGet(data, Encoding.UTF8.GetBytes(key), out byte[] val), Is.True, $"Key {key} not found via TryGet");
            Assert.That(val, Is.EqualTo(value), $"TryGet value mismatch at key {key}");
        }
    }

    /// <summary>
    /// Regression: single-entry HSST with a value that crosses page boundaries.
    /// </summary>
    /// <remarks>
    /// One entry whose value is large enough to push the writer many pages past
    /// the entry's flag byte. Without the trigger-3 single-entry short-circuit
    /// in <see cref="HsstBTreeBuilder{TWriter, TReader, TPin}"/>.Build,
    /// FlushPendingNotOnCurrentPage drains the lone pending entry as a direct
    /// Entry descriptor and EmitInlineLeaf never runs. BuildIndex's
    /// currentNative.Count == 1 early-return then returns
    /// <c>absoluteIndexStart - only.ChildOffset</c> — the entry record's full
    /// byte length (1 + keyLen + LEB128 + valueLen) — as the rootSize, which
    /// overflows the u16 trailer field for any value past ~64 KiB. Covers both
    /// key-first and key-after-value layouts since both flow through the same
    /// trigger-3 path.
    /// </remarks>
    [TestCase(16, false)]            // small value (fits page) — sanity baseline
    [TestCase(6 * 1024, false)]      // ~2-page value, key-after-value
    [TestCase(150 * 1024, false)]    // ~37 pages, key-after-value (was: u16 overflow)
    [TestCase(16, true)]             // small value (fits page) — key-first sanity
    [TestCase(150 * 1024, true)]     // ~37 pages, key-first (matches failing snapshot shape)
    public void Build_OneEntry_PageCrossingValue_DoesNotOverflowRoot(int valueLen, bool keyFirst)
    {
        byte[] key = new byte[30];
        for (int i = 0; i < 30; i++) key[i] = (byte)(i + 1);
        byte[] value = new byte[valueLen];
        for (int j = 0; j < value.Length; j++) value[j] = (byte)((j * 31 + 7) & 0xFF);

        byte[] data = HsstTestUtil.BuildToArray(
            (ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
                builder.Add(key, value),
            keyLength: 30, keyFirst: keyFirst);

        Assert.That(TryGet(data, key, out byte[] got), Is.True, "Single entry not found via TryGet");
        Assert.That(got, Is.EqualTo(value), "Single entry value mismatch");

        List<(byte[] Key, byte[] Value)> all = Materialize(data);
        Assert.That(all.Count, Is.EqualTo(1));
        Assert.That(all[0].Key, Is.EqualTo(key));
        Assert.That(all[0].Value, Is.EqualTo(value));
    }

    [TestCase(1)]
    [TestCase(10)]
    [TestCase(200)]
    public void Enumeration_Returns_Sorted_Entries(int count)
    {
        List<(string Key, string Value)> entries = [];
        for (int i = 0; i < count; i++)
        {
            string key = $"key_{i:D6}";
            string value = $"val_{i}";
            entries.Add((key, value));
        }

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            foreach ((string key, string value) in entries)
            {
                builder.Add(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
            }
        });

        List<string> expectedKeys = entries.ConvertAll(e => e.Key);
        expectedKeys.Sort(StringComparer.Ordinal);

        List<(byte[] Key, byte[] Value)> actual = Materialize(data);
        Assert.That(actual.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
            Assert.That(Encoding.UTF8.GetString(actual[i].Key), Is.EqualTo(expectedKeys[i]));
    }

    [Test]
    public void Various_Value_Sizes()
    {
        // Same-length keys (uniform-key invariant); values vary from empty to ~10 KiB.
        byte[] longValue = new byte[10000];
        Random.Shared.NextBytes(longValue);

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            builder.Add("a"u8, ReadOnlySpan<byte>.Empty);
            builder.Add("b"u8, longValue);
            builder.Add("c"u8, "x"u8);
        });

        Assert.That(CountEntries(data), Is.EqualTo(3));

        Assert.That(TryGet(data, "a"u8, out byte[] v1), Is.True);
        Assert.That(v1.Length, Is.EqualTo(0));

        Assert.That(TryGet(data, "b"u8, out byte[] v2), Is.True);
        Assert.That(v2.AsSpan().SequenceEqual(longValue), Is.True);

        Assert.That(TryGet(data, "c"u8, out byte[] v3), Is.True);
        Assert.That(Encoding.UTF8.GetString(v3), Is.EqualTo("x"));
    }

    [TestCase(100, 42)]
    [TestCase(1000, 123)]
    [TestCase(5000, 999)]
    public void Binary_Keys_RoundTrip(int count, int seed)
    {
        Random rng = new(seed);
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            entries[i].Key = new byte[32];
            entries[i].Value = new byte[32];
            rng.NextBytes(entries[i].Key);
            rng.NextBytes(entries[i].Value);
        }
        Array.Sort(entries, (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            foreach ((byte[] key, byte[] value) in entries)
            {
                builder.Add(key, value);
            }
        });

        Assert.That(CountEntries(data), Is.EqualTo(count));

        foreach ((byte[] key, byte[] value) in entries)
        {
            Assert.That(TryGet(data, key, out byte[] val), Is.True);
            Assert.That(val.AsSpan().SequenceEqual(value), Is.True);
        }

        List<(byte[] Key, byte[] Value)> actual = Materialize(data);
        Assert.That(actual.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(actual[i].Key.AsSpan().SequenceEqual(entries[i].Key), Is.True);
            Assert.That(actual[i].Value.AsSpan().SequenceEqual(entries[i].Value), Is.True);
        }
    }

    /// <summary>
    /// Regression test for internal node boundary separator bug.
    /// </summary>
    [Test]
    public void Binary_Keys_SmallLeaf_RoundTrip()
    {
        (string Key, string Value)[] hexEntries =
        [
            ("6C3A850F2A4303CEBEFC75F9B169ACB5A07E12F84F6CC55DFAFC9AE609EED608", "F9FF8903DBBD1C853B1890B3CA2C73D23739913597EB1C007527152EA91CC4D0"),
            ("7374A05BF4BBD243F66331CF6F11E06DFC3D3E8BCD6D3658B8C0B76651D29E34", "193CACB56E5C0B2B740A2023E46F7C99C75BC73062FC90063D47A233046CF123"),
            ("738F9ED9F043D768AFD784BD11F7C9018A8EFE476FB3B01D804B4E0BDB1652BE", "A49E2265C7C899BDC359B364BDCFD53F77AA2A981978C5BFDF8058A5F5CB8C99"),
            ("7A8B29876DFAC78D26FC5F3831BAB1F4C60DFBEDD136B05BA4A8A56CF9E44C2D", "9DD3F80D7D63230198B8A8FEBCD81AA48CFC616F5628F343DBCEE3C5555B9442"),
            ("7A8B49E56B67F911A381C08315CD3629A3F325C7C3E0C1706C14D6C9CAF8367D", "15A35D6966D927BAAE1E43B59C2AB552B76FCFE9CE8A3D99CAD97957903047AB"),
            ("82B8686069E521734064E0BB203C6C6C014F8ECBC90977A28F1B637D0BE0370E", "DAEF0267D21A77A154992BE299ACD41BFB14E494EBC37D7841C5D04E81A3685F"),
            ("84C61872D56339C1F4418316004B5FB0750E9430EBB9A52BD96286466FF4C7F8", "CC1ADFF7B7636A137068A3D7F4AFBF9321A730E7375CADCB20ED9972DDF35200"),
            ("9A3F37BBBE6820FE83BE2B55F78AC9B64FA4C24637B0A6A0B7203DA68728A5CC", "CB7EDAB045ACA26B99923FF2F17B9A8720E015B5603CD8EA9896049D2B79775A"),
        ];

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            foreach ((string key, string value) in hexEntries)
                builder.Add(Convert.FromHexString(key), Convert.FromHexString(value));
        });

        Assert.That(CountEntries(data), Is.EqualTo(hexEntries.Length));

        foreach ((string key, string value) in hexEntries)
        {
            byte[] keyBytes = Convert.FromHexString(key);
            Assert.That(TryGet(data, keyBytes, out byte[] val), Is.True, $"Key {key} not found");
            Assert.That(val.AsSpan().SequenceEqual(Convert.FromHexString(value)), Is.True);
        }

        List<(byte[] Key, byte[] Value)> actual = Materialize(data);
        Assert.That(actual.Count, Is.EqualTo(hexEntries.Length));
        for (int i = 0; i < hexEntries.Length; i++)
        {
            Assert.That(actual[i].Key.AsSpan().SequenceEqual(Convert.FromHexString(hexEntries[i].Key)), Is.True);
            Assert.That(actual[i].Value.AsSpan().SequenceEqual(Convert.FromHexString(hexEntries[i].Value)), Is.True);
        }
    }

    [TestCase(100, 32, 32, 42)]
    [TestCase(300, 32, 32, 77)]
    [TestCase(200, 64, 128, 55)]
    [TestCase(500, 64, 128, 101)]
    [TestCase(1000, 64, 128, 202)]
    public void Binary_Keys_MultiLevel_And_VariableSize_RoundTrip(int count, int keyLen, int maxValLen, int seed)
    {
        // Keys are now uniform-length per HSST; this test still exercises multi-level
        // B-tree builds with variable-length values.
        Random rng = new(seed);
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            int valLen = rng.Next(0, maxValLen + 1);
            entries[i].Key = new byte[keyLen];
            entries[i].Value = new byte[valLen];
            rng.NextBytes(entries[i].Key);
            rng.NextBytes(entries[i].Value);
        }
        Array.Sort(entries, (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));

        List<(byte[] Key, byte[] Value)> deduped = new(count);
        for (int i = 0; i < entries.Length; i++)
        {
            if (i + 1 < entries.Length && entries[i].Key.AsSpan().SequenceEqual(entries[i + 1].Key))
                continue;
            deduped.Add(entries[i]);
        }

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            foreach ((byte[] key, byte[] value) in deduped)
                builder.Add(key, value);
        });

        Assert.That(CountEntries(data), Is.EqualTo(deduped.Count));

        foreach ((byte[] key, byte[] value) in deduped)
        {
            Assert.That(TryGet(data, key, out byte[] val), Is.True,
                $"Key {BitConverter.ToString(key)} not found");
            Assert.That(val.AsSpan().SequenceEqual(value), Is.True);
        }

        List<(byte[] Key, byte[] Value)> actual = Materialize(data);
        Assert.That(actual.Count, Is.EqualTo(deduped.Count));
        for (int i = 0; i < deduped.Count; i++)
        {
            Assert.That(actual[i].Key.AsSpan().SequenceEqual(deduped[i].Key), Is.True);
            Assert.That(actual[i].Value.AsSpan().SequenceEqual(deduped[i].Value), Is.True);
        }
    }

    [TestCase(100, 32, 32, 42)]
    [TestCase(200, 20, 64, 55)]
    [TestCase(500, 52, 32, 101)]
    public void Binary_Keys_RoundTrip_VariedShapes(int count, int keyLen, int maxValLen, int seed)
    {
        Random rng = new(seed);
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            entries[i].Key = new byte[keyLen];
            entries[i].Value = new byte[rng.Next(0, maxValLen + 1)];
            rng.NextBytes(entries[i].Key);
            rng.NextBytes(entries[i].Value);
        }
        Array.Sort(entries, (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));

        List<(byte[] Key, byte[] Value)> deduped = new(count);
        for (int i = 0; i < entries.Length; i++)
        {
            if (i + 1 < entries.Length && entries[i].Key.AsSpan().SequenceEqual(entries[i + 1].Key))
                continue;
            deduped.Add(entries[i]);
        }

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            foreach ((byte[] key, byte[] value) in deduped)
                builder.Add(key, value);
        });

        Assert.That(CountEntries(data), Is.EqualTo(deduped.Count));

        foreach ((byte[] key, byte[] value) in deduped)
        {
            Assert.That(TryGet(data, key, out byte[] val), Is.True,
                $"Key {BitConverter.ToString(key)} not found");
            Assert.That(val.AsSpan().SequenceEqual(value), Is.True);
        }

        HashSet<byte[]> existingKeys = new(deduped.ConvertAll(e => e.Key), new ByteArrayComparer());
        Random negRng = new(seed + 9999);
        int negChecked = 0;
        while (negChecked < 50)
        {
            byte[] randomKey = new byte[keyLen];
            negRng.NextBytes(randomKey);
            if (existingKeys.Contains(randomKey)) continue;
            Assert.That(TryGet(data, randomKey, out _), Is.False,
                $"Non-existent key {BitConverter.ToString(randomKey)} falsely found");
            negChecked++;
        }

        List<(byte[] Key, byte[] Value)> actual = Materialize(data);
        Assert.That(actual.Count, Is.EqualTo(deduped.Count));
        for (int i = 0; i < deduped.Count; i++)
        {
            Assert.That(actual[i].Key.AsSpan().SequenceEqual(deduped[i].Key), Is.True);
            Assert.That(actual[i].Value.AsSpan().SequenceEqual(deduped[i].Value), Is.True);
        }
    }

    [TestCase(100, 32, 32, 42)]
    [TestCase(300, 32, 32, 77)]
    public void Binary_Keys_MultiLevel_RoundTrip(int count, int keyLen, int maxValLen, int seed)
    {
        Random rng = new(seed);
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            entries[i].Key = new byte[keyLen];
            entries[i].Value = new byte[rng.Next(0, maxValLen + 1)];
            rng.NextBytes(entries[i].Key);
            rng.NextBytes(entries[i].Value);
        }
        Array.Sort(entries, (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));

        List<(byte[] Key, byte[] Value)> deduped = new(count);
        for (int i = 0; i < entries.Length; i++)
        {
            if (i + 1 < entries.Length && entries[i].Key.AsSpan().SequenceEqual(entries[i + 1].Key))
                continue;
            deduped.Add(entries[i]);
        }

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            foreach ((byte[] key, byte[] value) in deduped)
                builder.Add(key, value);
        });

        Assert.That(CountEntries(data), Is.EqualTo(deduped.Count));

        foreach ((byte[] key, byte[] value) in deduped)
        {
            Assert.That(TryGet(data, key, out byte[] val), Is.True,
                $"Key {BitConverter.ToString(key)} not found");
            Assert.That(val.AsSpan().SequenceEqual(value), Is.True);
        }

        HashSet<byte[]> existingKeys = new(deduped.ConvertAll(e => e.Key), new ByteArrayComparer());
        Random negRng = new(seed + 9999);
        int negChecked = 0;
        while (negChecked < 50)
        {
            byte[] randomKey = new byte[keyLen];
            negRng.NextBytes(randomKey);
            if (existingKeys.Contains(randomKey)) continue;
            Assert.That(TryGet(data, randomKey, out _), Is.False);
            negChecked++;
        }

        List<(byte[] Key, byte[] Value)> actual = Materialize(data);
        Assert.That(actual.Count, Is.EqualTo(deduped.Count));
        for (int i = 0; i < deduped.Count; i++)
        {
            Assert.That(actual[i].Key.AsSpan().SequenceEqual(deduped[i].Key), Is.True);
            Assert.That(actual[i].Value.AsSpan().SequenceEqual(deduped[i].Value), Is.True);
        }
    }

    [Test]
    public void Duplicate_Keys_LastWriteWins()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            builder.Add("key"u8, "value1"u8);
            builder.Add("key"u8, "value2"u8);
        });

        Assert.That(CountEntries(data), Is.EqualTo(2));
    }

    [Test]
    public void NestedHsst_RoundTrip()
    {
        byte[] innerData = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            builder.Add([0x01, 0x02], [0xAA, 0xBB]);
        });

        byte[] outerData = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            builder.Add([0x00], innerData);
        });

        Assert.That(CountEntries(outerData), Is.EqualTo(1));
        Assert.That(TryGet(outerData, [0x00], out byte[] columnData), Is.True);
        Assert.That(columnData, Is.EqualTo(innerData));

        Assert.That(CountEntries(columnData), Is.EqualTo(1));
        Assert.That(TryGet(columnData, [0x01, 0x02], out byte[] value), Is.True);
        Assert.That(value, Is.EqualTo(new byte[] { 0xAA, 0xBB }));
    }

    [Test]
    public void NestedHsst_MultipleColumns_RoundTrip()
    {
        byte[] addr = new byte[20];
        addr[0] = 0xAB;
        addr[19] = 0xCD;
        byte[] accountRlp = new byte[50];
        accountRlp[0] = 0xC0;
        for (int i = 1; i < 50; i++) accountRlp[i] = (byte)(i & 0xFF);

        byte[] accountsInner = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            builder.Add(addr, accountRlp);
        });

        byte[] emptyInner = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) => { });

        byte[] outerData = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            builder.Add([0x00], accountsInner);
            builder.Add([0x01], emptyInner);
            builder.Add([0x02], emptyInner);
            builder.Add([0x03], emptyInner);
            builder.Add([0x04], emptyInner);
            builder.Add([0x05], emptyInner);
            builder.Add([0x06], emptyInner);
            builder.Add([0x07], emptyInner);
            builder.Add([0x08], emptyInner);
        });

        Assert.That(CountEntries(outerData), Is.EqualTo(9));

        Assert.That(TryGet(outerData, [0x00], out byte[] columnData), Is.True);
        Assert.That(columnData.Length, Is.EqualTo(accountsInner.Length));
        Assert.That(columnData, Is.EqualTo(accountsInner));

        Assert.That(CountEntries(columnData), Is.EqualTo(1));
        Assert.That(TryGet(columnData, addr, out byte[] value), Is.True);
        Assert.That(value, Is.EqualTo(accountRlp));
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) =>
            x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            HashCode hash = new();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }

    [Test]
    public void FinishValueWrite_WithExplicitLength_TreatsLeadingBytesAsPadding()
    {
        // Caller writes pad bytes, then real value bytes, and declares only the
        // real-value length. The reader must surface only the real value, and
        // the orphan pad bytes must not be visible through the entry's bound.
        const int padLen = 17;
        byte[] realValue = "hello-padded-world"u8.ToArray();
        byte[] key = "k"u8.ToArray();

        using PooledByteBufferWriter pooled = new(4096);
        ref PooledByteBufferWriter.Writer writer = ref pooled.GetWriter();
        using HsstBTreeBuilderBuffersContainer buffers = new();
        HsstBTreeBuilder<PooledByteBufferWriter.Writer> b = new(ref writer, ref buffers.Buffers, keyLength: -1);
        try
        {
            ref PooledByteBufferWriter.Writer w = ref b.BeginValueWrite();
            // Pad with a recognisable filler so any leak into the value is obvious.
            Span<byte> pad = w.GetSpan(padLen);
            pad[..padLen].Fill(0xCC);
            w.Advance(padLen);
            // Real value bytes.
            Span<byte> dst = w.GetSpan(realValue.Length);
            realValue.AsSpan().CopyTo(dst);
            w.Advance(realValue.Length);
            b.FinishValueWrite(key, realValue.Length);
            b.Build();
        }
        finally { b.Dispose(); }

        ReadOnlySpan<byte> data = pooled.WrittenSpan;
        Assert.That(CountEntries(data), Is.EqualTo(1));
        Assert.That(TryGet(data, key, out byte[] got), Is.True);
        Assert.That(got, Is.EqualTo(realValue));
    }

    [Test]
    public void NestedBuilder_TwoLevel_RoundTrips()
    {
        // Outer HSST with one entry whose value is an inner HSST
        using PooledByteBufferWriter pooled = new(4096);
        ref PooledByteBufferWriter.Writer writer = ref pooled.GetWriter();
        using HsstBTreeBuilderBuffersContainer outerBuffers = new();
        HsstBTreeBuilder<PooledByteBufferWriter.Writer> outer = new(ref writer, ref outerBuffers.Buffers, keyLength: -1);
        try
        {
            ref PooledByteBufferWriter.Writer innerWriter = ref outer.BeginValueWrite();
            long innerStart = innerWriter.Written;
            using HsstBTreeBuilderBuffersContainer innerBuffers = new();
            using HsstBTreeBuilder<PooledByteBufferWriter.Writer> inner = new(ref innerWriter, ref innerBuffers.Buffers, keyLength: -1);
            inner.Add("key1"u8, "val1"u8);
            inner.Add("key2"u8, "val2"u8);
            inner.Build();
            outer.FinishValueWrite("tag"u8, innerWriter.Written - innerStart);
            outer.Build();
        }
        finally
        {
            outer.Dispose();
        }

        ReadOnlySpan<byte> outerSpan = pooled.WrittenSpan;
        Assert.That(CountEntries(outerSpan), Is.EqualTo(1));
        Assert.That(TryGet(outerSpan, "tag"u8, out byte[] innerData), Is.True);
        Assert.That(CountEntries(innerData), Is.EqualTo(2));
        Assert.That(TryGet(innerData, "key1"u8, out byte[] v1), Is.True);
        Assert.That(v1, Is.EqualTo("val1"u8.ToArray()));
    }

    [Test]
    public void NestedBuilder_MultipleColumns_SharedWriter_RoundTrips()
    {
        // Outer HSST with 3 columns, each an inner HSST built via shared writer
        using PooledByteBufferWriter pooled = new(65536);
        ref PooledByteBufferWriter.Writer writer = ref pooled.GetWriter();
        using HsstBTreeBuilderBuffersContainer outerBuffers = new();
        HsstBTreeBuilder<PooledByteBufferWriter.Writer> outer = new(ref writer, ref outerBuffers.Buffers, keyLength: -1);
        try
        {
            {
                ref PooledByteBufferWriter.Writer iw = ref outer.BeginValueWrite();
                long start = iw.Written;
                using HsstBTreeBuilderBuffersContainer innerBuffers = new();
                using HsstBTreeBuilder<PooledByteBufferWriter.Writer> inner = new(ref iw, ref innerBuffers.Buffers, keyLength: -1);
                inner.Add("from"u8, "block0"u8);
                inner.Add("to\0\0"u8, "block1"u8);
                inner.Build();
                outer.FinishValueWrite([0x00], iw.Written - start);
            }
            {
                ref PooledByteBufferWriter.Writer iw = ref outer.BeginValueWrite();
                long start = iw.Written;
                using HsstBTreeBuilderBuffersContainer innerBuffers = new();
                using HsstBTreeBuilder<PooledByteBufferWriter.Writer> inner = new(ref iw, ref innerBuffers.Buffers, keyLength: -1);
                byte[] addr = new byte[20]; addr[0] = 0xAB;
                inner.Add(addr, [0xC0, 0x80]);
                inner.Build();
                outer.FinishValueWrite([0x01], iw.Written - start);
            }
            {
                ref PooledByteBufferWriter.Writer iw = ref outer.BeginValueWrite();
                long start = iw.Written;
                using HsstBTreeBuilderBuffersContainer innerBuffers = new();
                using HsstBTreeBuilder<PooledByteBufferWriter.Writer> inner = new(ref iw, ref innerBuffers.Buffers, keyLength: -1);
                inner.Build();
                outer.FinishValueWrite([0x02], iw.Written - start);
            }
            outer.Build();
        }
        finally { outer.Dispose(); }

        ReadOnlySpan<byte> outerSpan = pooled.WrittenSpan;
        Assert.That(CountEntries(outerSpan), Is.EqualTo(3));
        Assert.That(TryGet(outerSpan, [0x00], out byte[] col0), Is.True, "col0");
        Assert.That(CountEntries(col0), Is.EqualTo(2));
        Assert.That(TryGet(col0, "from"u8, out byte[] fromVal), Is.True);
        Assert.That(TryGet(col0, "to\0\0"u8, out byte[] toVal), Is.True);
        Assert.That(toVal, Is.EqualTo("block1"u8.ToArray()));
        Assert.That(fromVal, Is.EqualTo("block0"u8.ToArray()));
        Assert.That(TryGet(outerSpan, [0x01], out _), Is.True, "col1");
        Assert.That(TryGet(outerSpan, [0x02], out _), Is.True, "col2");
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(127)]
    [TestCase(128)]
    [TestCase(254)]
    [TestCase(255)]
    public void Key_Length_Boundary_RoundTrips(int keyLength)
    {
        byte[] key = new byte[keyLength];
        for (int i = 0; i < keyLength; i++) key[i] = (byte)(i & 0xFF);
        byte[] value = "v"u8.ToArray();

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            builder.Add(key, value);
        });

        Assert.That(CountEntries(data), Is.EqualTo(1));
        Assert.That(TryGet(data, key, out byte[] got), Is.True);
        Assert.That(got, Is.EqualTo(value));
    }

    [TestCase(256)]
    [TestCase(1024)]
    public void Key_Longer_Than_255_Bytes_Throws(int keyLength)
    {
        byte[] key = new byte[keyLength];
        byte[] value = "v"u8.ToArray();

        Assert.That(() =>
            HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
            {
                builder.Add(key, value);
            }),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }
}
