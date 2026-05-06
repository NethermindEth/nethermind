// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstReaderTests
{
    private static byte[] BuildHsst(params (string Key, string Value)[] entries)
        => HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            foreach ((string key, string value) in entries)
                builder.Add(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
        });

    private static string ReadValue(ref SpanByteReader reader)
    {
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Span<byte> buf = new byte[r.GetBound().Length];
        r.GetValue(buf);
        return Encoding.UTF8.GetString(buf);
    }

    [TestCase("a", "alpha")]
    [TestCase("key1", "value1")]
    public void TrySeek_ExactMatch_ReadsCorrectValue(string key, string value)
    {
        byte[] data = BuildHsst(("a", "alpha"), ("b", "beta"), ("key1", "value1"), ("key2", "value2"));
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);

        Assert.That(r.TrySeek(Encoding.UTF8.GetBytes(key), out _), Is.True);
        Span<byte> buf = new byte[r.GetBound().Length];
        r.GetValue(buf);
        Assert.That(Encoding.UTF8.GetString(buf), Is.EqualTo(value));
    }

    [Test]
    public void TrySeek_BeforeFirstEntry_ReturnsFalse()
    {
        byte[] data = BuildHsst(("b", "beta"), ("c", "gamma"));
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);

        Assert.That(r.TrySeek("a"u8, out _), Is.False);
    }

    [Test]
    public void TrySeekFloor_AfterLastEntry_ReturnsLastEntry()
    {
        byte[] data = BuildHsst(("a", "alpha"), ("b", "beta"));
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);

        Assert.That(r.TrySeekFloor("z"u8, out _), Is.True);
        Span<byte> buf = new byte[r.GetBound().Length];
        r.GetValue(buf);
        Assert.That(Encoding.UTF8.GetString(buf), Is.EqualTo("beta"));

        // Exact TrySeek for the same non-existent key returns false.
        r.SetBound(new Bound(0, data.Length));
        Assert.That(r.TrySeek("z"u8, out _), Is.False);
    }

    [Test]
    public void TrySeekFloor_BetweenKeys_ReturnsFloorEntry()
    {
        byte[] data = BuildHsst(("a", "alpha"), ("c", "gamma"));
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);

        // "b" is between "a" and "c" — floor is "a"
        Assert.That(r.TrySeekFloor("b"u8, out _), Is.True);
        Span<byte> buf = new byte[r.GetBound().Length];
        r.GetValue(buf);
        Assert.That(Encoding.UTF8.GetString(buf), Is.EqualTo("alpha"));

        // Exact TrySeek for "b" returns false.
        r.SetBound(new Bound(0, data.Length));
        Assert.That(r.TrySeek("b"u8, out _), Is.False);
    }

    [Test]
    public void PreviousBound_AllowsRestoreAndReseek()
    {
        byte[] data = BuildHsst(("a", "alpha"), ("b", "beta"), ("c", "gamma"));
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);

        // Seek to "a", save root bound
        r.TrySeek("a"u8, out Bound rootBound);
        Bound aBound = r.GetBound();

        // Seek to "c", capturing "a"'s bound as previous
        r.SetBound(rootBound);
        r.TrySeek("c"u8, out _);
        Span<byte> buf = new byte[r.GetBound().Length];
        r.GetValue(buf);
        Assert.That(Encoding.UTF8.GetString(buf), Is.EqualTo("gamma"));

        // Restore to "a" bound and read
        r.SetBound(aBound);
        Span<byte> buf2 = new byte[r.GetBound().Length];
        r.GetValue(buf2);
        Assert.That(Encoding.UTF8.GetString(buf2), Is.EqualTo("alpha"));
    }

    [TestCase(1)]
    [TestCase(10)]
    [TestCase(65)]   // forces multi-level B-tree
    [TestCase(200)]
    [TestCase(1000)]
    public void TrySeek_MatchesHsst_TryGet_ForAllEntries(int count)
    {
        (string Key, string Value)[] entries = new (string, string)[count];
        for (int i = 0; i < count; i++)
            entries[i] = ($"key_{i:D6}", $"val_{i:D6}");

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            foreach ((string key, string value) in entries)
                builder.Add(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
        });

        SpanByteReader reader = new(data);

        foreach ((string key, string value) in entries)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] spanVal = Encoding.UTF8.GetBytes(value);

            using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            Bound root = r.GetBound();
            Assert.That(r.TrySeek(keyBytes, out _), Is.True, $"TrySeek failed for {key}");
            Span<byte> buf = new byte[r.GetBound().Length];
            r.GetValue(buf);
            Assert.That(buf.SequenceEqual(spanVal), Is.True, $"Value mismatch for {key}");
        }
    }

    [Test]
    public void GetValue_PartialBuffer_ReturnsMinLength()
    {
        byte[] data = BuildHsst(("key", "hello"));
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);

        r.TrySeek("key"u8, out _);
        Assert.That(r.GetBound().Length, Is.EqualTo(5)); // "hello"

        Span<byte> small = new byte[3];
        int written = r.GetValue(small);
        Assert.That(written, Is.EqualTo(3));
        Assert.That(Encoding.UTF8.GetString(small), Is.EqualTo("hel"));
    }

    [Test]
    public void GetBound_SetBound_RoundTrip()
    {
        byte[] data = BuildHsst(("a", "alpha"), ("b", "beta"));
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);

        Bound original = r.GetBound();
        r.TrySeek("b"u8, out _);
        Bound sought = r.GetBound();
        Assert.That(sought, Is.Not.EqualTo(original));

        r.SetBound(original);
        Assert.That(r.GetBound(), Is.EqualTo(original));
    }

    [Test]
    public void NestedHsst_Traversal_TwoLevels()
    {
        // Simulate a column HSST containing per-address inner HSSTs
        // Inner HSST for address "addr1": { "subtag1" -> "v1", "subtag2" -> "v2" }
        byte[] innerData1 = BuildHsst(("subtag1", "v1"), ("subtag2", "v2"));
        byte[] innerData2 = BuildHsst(("subtag1", "x1"));

        byte[] outerData = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            builder.Add("addr1"u8, innerData1);
            builder.Add("addr2"u8, innerData2);
        });

        SpanByteReader reader = new(outerData);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);

        // Descend into "addr1"
        Assert.That(r.TrySeek("addr1"u8, out Bound outerBound), Is.True);
        Bound addr1Bound = r.GetBound();

        // addr1Bound now points to innerData1 bytes within outerData
        // Navigate the inner HSST
        r.TrySeek("subtag2"u8, out _);
        Span<byte> buf = new byte[r.GetBound().Length];
        r.GetValue(buf);
        Assert.That(Encoding.UTF8.GetString(buf), Is.EqualTo("v2"));

        // Restore to outer and descend into "addr2"
        r.SetBound(outerBound);
        r.TrySeek("addr2"u8, out _);
        Bound addr2Bound = r.GetBound();

        r.TrySeek("subtag1"u8, out _);
        Span<byte> buf2 = new byte[r.GetBound().Length];
        r.GetValue(buf2);
        Assert.That(Encoding.UTF8.GetString(buf2), Is.EqualTo("x1"));
    }

    // --- 1:1 mirrors of HsstTests ---

    [Test]
    public void Empty_Hsst_TrySeek_ReturnsFalse()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) => { });
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Assert.That(r.TrySeek("hello"u8, out _), Is.False);
    }

    [Test]
    public void IndexType_Byte_Is_BTree_ReaderWorks()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
            builder.Add("key"u8, "value"u8));
        Assert.That(data[^1], Is.EqualTo((byte)IndexType.BTree));
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Assert.That(r.TrySeek("key"u8, out _), Is.True);
    }

    [Test]
    public void Single_Entry_RoundTrip_Reader()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
            builder.Add("key1"u8, "value1"u8));
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Bound root = r.GetBound();

        // Exact match
        Assert.That(r.TrySeek("key1"u8, out _), Is.True);
        Span<byte> buf = new byte[r.GetBound().Length];
        r.GetValue(buf);
        Assert.That(Encoding.UTF8.GetString(buf), Is.EqualTo("value1"));

        // Before first entry (use key with entirely different prefix so floor is empty)
        r.SetBound(root);
        Assert.That(r.TrySeek("aaa"u8, out _), Is.False);

        // After last entry - exact returns false; floor returns "key1"
        r.SetBound(root);
        Assert.That(r.TrySeek("key2"u8, out _), Is.False);
        r.SetBound(root);
        Assert.That(r.TrySeekFloor("key2"u8, out _), Is.True);
        Span<byte> buf2 = new byte[r.GetBound().Length];
        r.GetValue(buf2);
        Assert.That(Encoding.UTF8.GetString(buf2), Is.EqualTo("value1"));
    }

    [TestCase(2)]
    [TestCase(10)]
    [TestCase(64)]
    [TestCase(65)]
    [TestCase(128)]
    [TestCase(200)]
    [TestCase(1000)]
    [TestCase(5000)]
    public void Multiple_Entries_RoundTrip_Reader(int count)
    {
        List<(string Key, string Value)> expected = new();
        for (int i = 0; i < count; i++)
            expected.Add(($"key_{i:D6}", $"val_{i:D6}"));

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            foreach ((string key, string value) in expected)
                builder.Add(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
        });

        expected.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Bound root = r.GetBound();

        foreach ((string key, string value) in expected)
        {
            r.SetBound(root);
            Assert.That(r.TrySeek(Encoding.UTF8.GetBytes(key), out _), Is.True, $"Key {key} not found");
            Span<byte> buf = new byte[r.GetBound().Length];
            r.GetValue(buf);
            Assert.That(Encoding.UTF8.GetString(buf), Is.EqualTo(value), $"Value mismatch for {key}");
        }

        // Key before all entries returns false
        r.SetBound(root);
        Assert.That(r.TrySeek(""u8, out _), Is.False);
    }

    [Test]
    public void Various_Key_Value_Sizes_Reader()
    {
        byte[] longValue = new byte[10000];
        Random.Shared.NextBytes(longValue);
        byte[] longKey = new byte[255];
        for (int i = 0; i < longKey.Length; i++) longKey[i] = (byte)'c';

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            builder.Add("a"u8, ReadOnlySpan<byte>.Empty);
            builder.Add("b"u8, longValue);
            builder.Add(longKey, "x"u8);
        });

        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Bound root = r.GetBound();

        r.SetBound(root);
        Assert.That(r.TrySeek("a"u8, out _), Is.True);
        Assert.That(r.GetBound().Length, Is.EqualTo(0));

        r.SetBound(root);
        Assert.That(r.TrySeek("b"u8, out _), Is.True);
        Span<byte> v2 = new byte[r.GetBound().Length];
        r.GetValue(v2);
        Assert.That(v2.SequenceEqual(longValue), Is.True);

        r.SetBound(root);
        Assert.That(r.TrySeek(longKey, out _), Is.True);
        Span<byte> v3 = new byte[r.GetBound().Length];
        r.GetValue(v3);
        Assert.That(Encoding.UTF8.GetString(v3), Is.EqualTo("x"));
    }

    [TestCase(100, 42)]
    [TestCase(1000, 123)]
    [TestCase(5000, 999)]
    public void Binary_Keys_RoundTrip_Reader(int count, int seed)
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

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            foreach ((byte[] key, byte[] value) in entries)
                builder.Add(key, value);
        });

        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Bound root = r.GetBound();

        foreach ((byte[] key, byte[] value) in entries)
        {
            r.SetBound(root);
            Assert.That(r.TrySeek(key, out _), Is.True, $"Key {BitConverter.ToString(key)} not found");
            Span<byte> buf = new byte[r.GetBound().Length];
            r.GetValue(buf);
            Assert.That(buf.SequenceEqual(value), Is.True);
        }
    }

    [Test]
    public void Binary_Keys_SmallLeaf_RoundTrip_Reader()
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

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            foreach ((string key, string value) in hexEntries)
                builder.Add(Convert.FromHexString(key), Convert.FromHexString(value));
        }, maxLeafEntries: 4);

        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Bound root = r.GetBound();

        foreach ((string key, string value) in hexEntries)
        {
            byte[] keyBytes = Convert.FromHexString(key);
            r.SetBound(root);
            Assert.That(r.TrySeek(keyBytes, out _), Is.True, $"Key {key} not found");
            Span<byte> buf = new byte[r.GetBound().Length];
            r.GetValue(buf);
            Assert.That(buf.SequenceEqual(Convert.FromHexString(value)), Is.True);
        }
    }

    [TestCase(100, 4, 32, 32, 42)]
    [TestCase(300, 4, 32, 32, 77)]
    [TestCase(200, 4, 64, 128, 55)]
    [TestCase(500, 8, 64, 128, 101)]
    [TestCase(1000, 64, 64, 128, 202)]
    public void Binary_Keys_MultiLevel_And_VariableSize_RoundTrip_Reader(int count, int maxLeafEntries, int maxKeyLen, int maxValLen, int seed)
    {
        Random rng = new(seed);
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            entries[i].Key = new byte[rng.Next(1, maxKeyLen + 1)];
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

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            foreach ((byte[] key, byte[] value) in deduped)
                builder.Add(key, value);
        }, maxLeafEntries);

        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Bound root = r.GetBound();

        foreach ((byte[] key, byte[] value) in deduped)
        {
            r.SetBound(root);
            Assert.That(r.TrySeek(key, out _), Is.True, $"Key {BitConverter.ToString(key)} not found");
            Span<byte> buf = new byte[r.GetBound().Length];
            r.GetValue(buf);
            Assert.That(buf.SequenceEqual(value), Is.True);
        }
    }

    [TestCase(100, 32, 32, 42, 0)]
    [TestCase(100, 32, 32, 42, 2)]
    [TestCase(100, 32, 32, 42, 30)]
    [TestCase(200, 20, 64, 55, 18)]
    [TestCase(500, 52, 32, 101, 50)]
    public void Binary_Keys_WithMinSeparatorLength_RoundTrip_Reader(int count, int keyLen, int maxValLen, int seed, int minSepLen)
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

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            foreach ((byte[] key, byte[] value) in deduped)
                builder.Add(key, value);
        }, minSeparatorLength: minSepLen);

        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Bound root = r.GetBound();

        foreach ((byte[] key, byte[] value) in deduped)
        {
            r.SetBound(root);
            Assert.That(r.TrySeek(key, out _), Is.True, $"Key {BitConverter.ToString(key)} not found");
            Span<byte> buf = new byte[r.GetBound().Length];
            r.GetValue(buf);
            Assert.That(buf.SequenceEqual(value), Is.True);
        }
    }

    [TestCase(100, 4, 32, 32, 42, 30)]
    [TestCase(300, 4, 32, 32, 77, 30)]
    public void Binary_Keys_MultiLevel_WithMinSeparatorLength_RoundTrip_Reader(int count, int maxLeaf, int keyLen, int maxValLen, int seed, int minSepLen)
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

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            foreach ((byte[] key, byte[] value) in deduped)
                builder.Add(key, value);
        }, maxLeafEntries: maxLeaf, minSeparatorLength: minSepLen);

        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Bound root = r.GetBound();

        foreach ((byte[] key, byte[] value) in deduped)
        {
            r.SetBound(root);
            Assert.That(r.TrySeek(key, out _), Is.True, $"Key {BitConverter.ToString(key)} not found");
            Span<byte> buf = new byte[r.GetBound().Length];
            r.GetValue(buf);
            Assert.That(buf.SequenceEqual(value), Is.True);
        }
    }

    [Test]
    public void Duplicate_Keys_SeeksToAValue()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            builder.Add("key"u8, "value1"u8);
            builder.Add("key"u8, "value2"u8);
        });
        SpanByteReader reader = new(data);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Assert.That(r.TrySeek("key"u8, out _), Is.True);
        Assert.That(r.GetBound().Length, Is.GreaterThan(0));
    }

    [Test]
    public void NestedHsst_RoundTrip_Reader()
    {
        byte[] innerData = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
            builder.Add([0x01, 0x02], [0xAA, 0xBB]));

        byte[] outerData = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
            builder.Add([0x00], innerData));

        SpanByteReader reader = new(outerData);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);

        Assert.That(r.TrySeek([0x00], out Bound outerBound), Is.True);
        Assert.That(r.TrySeek([0x01, 0x02], out _), Is.True);
        Span<byte> buf = new byte[r.GetBound().Length];
        r.GetValue(buf);
        Assert.That(buf.ToArray(), Is.EqualTo(new byte[] { 0xAA, 0xBB }));
    }

    [Test]
    public void NestedHsst_MultipleColumns_RoundTrip_Reader()
    {
        byte[] addr = new byte[20];
        addr[0] = 0xAB;
        addr[19] = 0xCD;
        byte[] accountRlp = new byte[50];
        accountRlp[0] = 0xC0;
        for (int i = 1; i < 50; i++) accountRlp[i] = (byte)(i & 0xFF);

        byte[] accountsInner = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
            builder.Add(addr, accountRlp));
        byte[] emptyInner = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) => { });

        byte[] outerData = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            builder.Add([0x00], accountsInner);
            for (byte b = 0x01; b <= 0x08; b++)
                builder.Add([b], emptyInner);
        });

        SpanByteReader reader = new(outerData);
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Bound root = r.GetBound();

        Assert.That(r.TrySeek([0x00], out Bound outerBound), Is.True);
        Assert.That(r.GetBound().Length, Is.EqualTo(accountsInner.Length));

        Assert.That(r.TrySeek(addr, out _), Is.True);
        Span<byte> buf = new byte[r.GetBound().Length];
        r.GetValue(buf);
        Assert.That(buf.ToArray(), Is.EqualTo(accountRlp));
    }

    [Test]
    public void NestedBuilder_TwoLevel_RoundTrips_Reader()
    {
        byte[] buffer = new byte[4096];
        SpanBufferWriter writer = new(buffer);
        HsstBuilder<SpanBufferWriter, SpanByteReader, NoOpPin> outer = new(ref writer);
        try
        {
            ref SpanBufferWriter innerWriter = ref outer.BeginValueWrite();
            using HsstBuilder<SpanBufferWriter, SpanByteReader, NoOpPin> inner = new(ref innerWriter);
            inner.Add("key1"u8, "val1"u8);
            inner.Add("key2"u8, "val2"u8);
            inner.Build();
            outer.FinishValueWrite("tag"u8);
            outer.Build();
        }
        finally { outer.Dispose(); }
        int len = (int)writer.Written;

        SpanByteReader reader = new(buffer.AsSpan(0, len));
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);

        Assert.That(r.TrySeek("tag"u8, out _), Is.True);
        Bound innerBound = r.GetBound();

        r.TrySeek("key1"u8, out _);
        Span<byte> v1 = new byte[r.GetBound().Length];
        r.GetValue(v1);
        Assert.That(v1.ToArray(), Is.EqualTo("val1"u8.ToArray()));

        r.SetBound(innerBound);
        r.TrySeek("key2"u8, out _);
        Span<byte> v2 = new byte[r.GetBound().Length];
        r.GetValue(v2);
        Assert.That(v2.ToArray(), Is.EqualTo("val2"u8.ToArray()));
    }

    [Test]
    public void NestedBuilder_MultipleColumns_SharedWriter_RoundTrips_Reader()
    {
        byte[] buffer = new byte[65536];
        SpanBufferWriter writer = new(buffer);
        HsstBuilder<SpanBufferWriter, SpanByteReader, NoOpPin> outer = new(ref writer);
        try
        {
            {
                ref SpanBufferWriter iw = ref outer.BeginValueWrite();
                using HsstBuilder<SpanBufferWriter, SpanByteReader, NoOpPin> inner = new(ref iw);
                inner.Add("from"u8, "block0"u8);
                inner.Add("to"u8, "block1"u8);
                inner.Build();
                outer.FinishValueWrite([0x00]);
            }
            {
                ref SpanBufferWriter iw = ref outer.BeginValueWrite();
                using HsstBuilder<SpanBufferWriter, SpanByteReader, NoOpPin> inner = new(ref iw);
                byte[] addr = new byte[20]; addr[0] = 0xAB;
                inner.Add(addr, [0xC0, 0x80]);
                inner.Build();
                outer.FinishValueWrite([0x01]);
            }
            {
                ref SpanBufferWriter iw = ref outer.BeginValueWrite();
                using HsstBuilder<SpanBufferWriter, SpanByteReader, NoOpPin> inner = new(ref iw);
                inner.Build();
                outer.FinishValueWrite([0x02]);
            }
            outer.Build();
        }
        finally { outer.Dispose(); }
        int len = (int)writer.Written;

        SpanByteReader reader = new(buffer.AsSpan(0, len));
        using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
        Bound root = r.GetBound();

        Assert.That(r.TrySeek([0x00], out Bound outerBound), Is.True, "col0");
        Bound col0Bound = r.GetBound();

        Assert.That(r.TrySeek("from"u8, out _), Is.True);
        Span<byte> fromVal = new byte[r.GetBound().Length];
        r.GetValue(fromVal);
        Assert.That(fromVal.ToArray(), Is.EqualTo("block0"u8.ToArray()));

        r.SetBound(root);
        Assert.That(r.TrySeek([0x01], out _), Is.True, "col1");
        r.SetBound(root);
        Assert.That(r.TrySeek([0x02], out _), Is.True, "col2");
    }

    /// <summary>
    /// Forces the copy/rent fallback path inside <see cref="HsstReader{TReader,TPin}.TryLoadNode"/>:
    /// every <see cref="IHsstByteReader{TPin}.PinBuffer"/> rents a pooled buffer and copies into it,
    /// instead of returning a zero-copy slice. Mirrors what a paged or stream-backed reader
    /// would do when a requested range can't be served as a contiguous span.
    /// </summary>
    private struct CopyOnlyByteReader(byte[] data) : IHsstByteReader<PooledArrayPin>
    {
        private readonly byte[] _data = data;

        public readonly long Length => _data.Length;

        public readonly bool TryRead(long offset, Span<byte> output)
        {
            if ((ulong)offset > (ulong)(_data.Length - output.Length)) return false;
            _data.AsSpan((int)offset, output.Length).CopyTo(output);
            return true;
        }

        public readonly bool TryReadWithReadahead(long offset, Span<byte> output) => TryRead(offset, output);

        public readonly PooledArrayPin PinBuffer(long offset, long size)
        {
            if ((ulong)offset + (ulong)size > (ulong)_data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            PooledArrayPin pin = PooledArrayPin.Rent((int)size, out Span<byte> rented);
            _data.AsSpan((int)offset, (int)size).CopyTo(rented);
            return pin;
        }
    }

    [TestCase(1)]
    [TestCase(64)]
    [TestCase(200)]
    [TestCase(1000)]
    public void CopyOnlyReader_TrySeek_ParityWithSpanReader(int count)
    {
        (string Key, string Value)[] entries = new (string, string)[count];
        for (int i = 0; i < count; i++)
            entries[i] = ($"key_{i:D6}", $"val_{i:D6}");

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            foreach ((string key, string value) in entries)
                builder.Add(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
        });

        CopyOnlyByteReader reader = new(data);
        using HsstReader<CopyOnlyByteReader, PooledArrayPin> r = new(in reader);
        Bound root = r.GetBound();

        foreach ((string key, string value) in entries)
        {
            r.SetBound(root);
            Assert.That(r.TrySeek(Encoding.UTF8.GetBytes(key), out _), Is.True, $"Key {key} not found");
            Span<byte> buf = new byte[r.GetBound().Length];
            r.GetValue(buf);
            Assert.That(Encoding.UTF8.GetString(buf), Is.EqualTo(value), $"Value mismatch for {key}");
        }

        // Floor for a key before all entries returns false even via the copy path.
        r.SetBound(root);
        Assert.That(r.TrySeek(""u8, out _), Is.False);
    }
}
