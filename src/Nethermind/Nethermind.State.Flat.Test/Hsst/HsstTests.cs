// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstTests
{
    [TestCase(0, 1)]
    [TestCase(1, 1)]
    [TestCase(127, 1)]
    [TestCase(128, 2)]
    [TestCase(255, 2)]
    [TestCase(16383, 2)]
    [TestCase(16384, 3)]
    [TestCase(int.MaxValue, 5)]
    public void Leb128_RoundTrip(int value, int expectedSize)
    {
        Assert.That(Leb128.EncodedSize(value), Is.EqualTo(expectedSize));

        byte[] buffer = new byte[16];
        int endPos = Leb128.Write(buffer, 0, value);
        Assert.That(endPos, Is.EqualTo(expectedSize));

        int readPos = 0;
        int decoded = Leb128.Read(buffer, ref readPos);
        Assert.That(decoded, Is.EqualTo(value));
        Assert.That(readPos, Is.EqualTo(expectedSize));
    }

    [Test]
    public void Empty_Hsst_HasZeroEntries()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) => { });

        Hsst.Hsst hsst = new(data);
        Assert.That(hsst.EntryCount, Is.EqualTo(0));
        Assert.That(hsst.TryGet("hello"u8, out _), Is.False);
    }

    [Test]
    public void Version_Byte_Is_One()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            builder.Add("key"u8, "value"u8);
        });

        Assert.That(data[0], Is.EqualTo(0x01));
    }

    [Test]
    public void Single_Entry_RoundTrip()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            builder.Add("key1"u8, "value1"u8);
        });

        Hsst.Hsst hsst = new(data);
        Assert.That(hsst.EntryCount, Is.EqualTo(1));

        Assert.That(hsst.TryGet("key1"u8, out ReadOnlySpan<byte> val), Is.True);
        Assert.That(Encoding.UTF8.GetString(val), Is.EqualTo("value1"));

        Assert.That(hsst.TryGet("key2"u8, out _), Is.False);
        Assert.That(hsst.TryGet("key0"u8, out _), Is.False);
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
        List<(string Key, string Value)> expected = new();
        for (int i = 0; i < count; i++)
        {
            string key = $"key_{i:D6}";
            string value = $"val_{i:D6}";
            expected.Add((key, value));
        }

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            foreach ((string key, string value) in expected)
            {
                builder.Add(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
            }
        });

        Hsst.Hsst hsst = new(data);
        Assert.That(hsst.EntryCount, Is.EqualTo(count));

        expected.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

        foreach ((string key, string value) in expected)
        {
            Assert.That(hsst.TryGet(Encoding.UTF8.GetBytes(key), out ReadOnlySpan<byte> val), Is.True, $"Key {key} not found");
            Assert.That(Encoding.UTF8.GetString(val), Is.EqualTo(value));
        }

        Assert.That(hsst.TryGet("zzz_not_exist"u8, out _), Is.False);
        Assert.That(hsst.TryGet(""u8, out _), Is.False);
    }

    [TestCase(1)]
    [TestCase(10)]
    [TestCase(200)]
    public void Enumeration_Returns_Sorted_Entries(int count)
    {
        List<(string Key, string Value)> entries = new();
        for (int i = 0; i < count; i++)
        {
            string key = $"key_{i:D6}";
            string value = $"val_{i}";
            entries.Add((key, value));
        }

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            foreach ((string key, string value) in entries)
            {
                builder.Add(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
            }
        });

        List<string> expectedKeys = entries.ConvertAll(e => e.Key);
        expectedKeys.Sort(StringComparer.Ordinal);

        Hsst.Hsst hsst = new(data);

        int idx = 0;
        foreach (Hsst.Hsst.KeyValueEntry entry in hsst)
        {
            Assert.That(Encoding.UTF8.GetString(entry.Key), Is.EqualTo(expectedKeys[idx]));
            idx++;
        }
        Assert.That(idx, Is.EqualTo(count));
    }

    [Test]
    public void Various_Key_Value_Sizes()
    {
        byte[] longValue = new byte[10000];
        Random.Shared.NextBytes(longValue);
        byte[] longKey = new byte[500];
        for (int i = 0; i < longKey.Length; i++) longKey[i] = (byte)'c';

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            builder.Add("a"u8, ReadOnlySpan<byte>.Empty);
            builder.Add("b"u8, longValue);
            builder.Add(longKey, "x"u8);
        });

        Hsst.Hsst hsst = new(data);
        Assert.That(hsst.EntryCount, Is.EqualTo(3));

        Assert.That(hsst.TryGet("a"u8, out ReadOnlySpan<byte> v1), Is.True);
        Assert.That(v1.Length, Is.EqualTo(0));

        Assert.That(hsst.TryGet("b"u8, out ReadOnlySpan<byte> v2), Is.True);
        Assert.That(v2.SequenceEqual(longValue), Is.True);

        Assert.That(hsst.TryGet(longKey, out ReadOnlySpan<byte> v3), Is.True);
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

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            foreach ((byte[] key, byte[] value) in entries)
            {
                builder.Add(key, value);
            }
        });

        Hsst.Hsst hsst = new(data);
        Assert.That(hsst.EntryCount, Is.EqualTo(count));

        foreach ((byte[] key, byte[] value) in entries)
        {
            Assert.That(hsst.TryGet(key, out ReadOnlySpan<byte> val), Is.True);
            Assert.That(val.SequenceEqual(value), Is.True);
        }

        int idx = 0;
        foreach (Hsst.Hsst.KeyValueEntry entry in hsst)
        {
            Assert.That(entry.Key.SequenceEqual(entries[idx].Key), Is.True);
            Assert.That(entry.Value.SequenceEqual(entries[idx].Value), Is.True);
            idx++;
        }
        Assert.That(idx, Is.EqualTo(count));
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

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            foreach ((string key, string value) in hexEntries)
                builder.Add(Convert.FromHexString(key), Convert.FromHexString(value));
        }, maxLeafEntries: 4);

        Hsst.Hsst hsst = new(data);
        Assert.That(hsst.EntryCount, Is.EqualTo(hexEntries.Length));

        foreach ((string key, string value) in hexEntries)
        {
            byte[] keyBytes = Convert.FromHexString(key);
            Assert.That(hsst.TryGet(keyBytes, out ReadOnlySpan<byte> val), Is.True, $"Key {key} not found");
            Assert.That(val.SequenceEqual(Convert.FromHexString(value)), Is.True);
        }

        int idx = 0;
        foreach (Hsst.Hsst.KeyValueEntry entry in hsst)
        {
            Assert.That(entry.Key.SequenceEqual(Convert.FromHexString(hexEntries[idx].Key)), Is.True);
            Assert.That(entry.Value.SequenceEqual(Convert.FromHexString(hexEntries[idx].Value)), Is.True);
            idx++;
        }
        Assert.That(idx, Is.EqualTo(hexEntries.Length));
    }

    [TestCase(100, 4, 32, 32, 42)]
    [TestCase(300, 4, 32, 32, 77)]
    [TestCase(200, 4, 64, 128, 55)]
    [TestCase(500, 8, 64, 128, 101)]
    [TestCase(1000, 64, 64, 128, 202)]
    public void Binary_Keys_MultiLevel_And_VariableSize_RoundTrip(int count, int maxLeafEntries, int maxKeyLen, int maxValLen, int seed)
    {
        Random rng = new(seed);
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            int keyLen = rng.Next(1, maxKeyLen + 1);
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

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            foreach ((byte[] key, byte[] value) in deduped)
                builder.Add(key, value);
        }, maxLeafEntries);

        Hsst.Hsst hsst = new(data);
        Assert.That(hsst.EntryCount, Is.EqualTo(deduped.Count));

        foreach ((byte[] key, byte[] value) in deduped)
        {
            Assert.That(hsst.TryGet(key, out ReadOnlySpan<byte> val), Is.True,
                $"Key {BitConverter.ToString(key)} not found");
            Assert.That(val.SequenceEqual(value), Is.True);
        }

        int idx = 0;
        foreach (Hsst.Hsst.KeyValueEntry entry in hsst)
        {
            Assert.That(entry.Key.SequenceEqual(deduped[idx].Key), Is.True);
            Assert.That(entry.Value.SequenceEqual(deduped[idx].Value), Is.True);
            idx++;
        }
        Assert.That(idx, Is.EqualTo(deduped.Count));
    }

    [TestCase(100, 32, 32, 42, 0)]
    [TestCase(100, 32, 32, 42, 2)]
    [TestCase(100, 32, 32, 42, 30)]
    [TestCase(200, 20, 64, 55, 18)]
    [TestCase(500, 52, 32, 101, 50)]
    public void Binary_Keys_WithMinSeparatorLength_RoundTrip(int count, int keyLen, int maxValLen, int seed, int minSepLen)
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

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            foreach ((byte[] key, byte[] value) in deduped)
                builder.Add(key, value);
        }, minSeparatorLength: minSepLen);

        Hsst.Hsst hsst = new(data);
        Assert.That(hsst.EntryCount, Is.EqualTo(deduped.Count));

        foreach ((byte[] key, byte[] value) in deduped)
        {
            Assert.That(hsst.TryGet(key, out ReadOnlySpan<byte> val), Is.True,
                $"Key {BitConverter.ToString(key)} not found");
            Assert.That(val.SequenceEqual(value), Is.True);
        }

        HashSet<byte[]> existingKeys = new(deduped.ConvertAll(e => e.Key), new ByteArrayComparer());
        Random negRng = new(seed + 9999);
        int negChecked = 0;
        while (negChecked < 50)
        {
            byte[] randomKey = new byte[keyLen];
            negRng.NextBytes(randomKey);
            if (existingKeys.Contains(randomKey)) continue;
            Assert.That(hsst.TryGet(randomKey, out _), Is.False,
                $"Non-existent key {BitConverter.ToString(randomKey)} falsely found");
            negChecked++;
        }

        int idx = 0;
        foreach (Hsst.Hsst.KeyValueEntry entry in hsst)
        {
            Assert.That(entry.Key.SequenceEqual(deduped[idx].Key), Is.True);
            Assert.That(entry.Value.SequenceEqual(deduped[idx].Value), Is.True);
            idx++;
        }
        Assert.That(idx, Is.EqualTo(deduped.Count));
    }

    [TestCase(100, 4, 32, 32, 42, 30)]
    [TestCase(300, 4, 32, 32, 77, 30)]
    public void Binary_Keys_MultiLevel_WithMinSeparatorLength_RoundTrip(int count, int maxLeaf, int keyLen, int maxValLen, int seed, int minSepLen)
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

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            foreach ((byte[] key, byte[] value) in deduped)
                builder.Add(key, value);
        }, maxLeafEntries: maxLeaf, minSeparatorLength: minSepLen);

        Hsst.Hsst hsst = new(data);
        Assert.That(hsst.EntryCount, Is.EqualTo(deduped.Count));

        foreach ((byte[] key, byte[] value) in deduped)
        {
            Assert.That(hsst.TryGet(key, out ReadOnlySpan<byte> val), Is.True,
                $"Key {BitConverter.ToString(key)} not found");
            Assert.That(val.SequenceEqual(value), Is.True);
        }

        HashSet<byte[]> existingKeys = new(deduped.ConvertAll(e => e.Key), new ByteArrayComparer());
        Random negRng = new(seed + 9999);
        int negChecked = 0;
        while (negChecked < 50)
        {
            byte[] randomKey = new byte[keyLen];
            negRng.NextBytes(randomKey);
            if (existingKeys.Contains(randomKey)) continue;
            Assert.That(hsst.TryGet(randomKey, out _), Is.False);
            negChecked++;
        }

        int idx = 0;
        foreach (Hsst.Hsst.KeyValueEntry entry in hsst)
        {
            Assert.That(entry.Key.SequenceEqual(deduped[idx].Key), Is.True);
            Assert.That(entry.Value.SequenceEqual(deduped[idx].Value), Is.True);
            idx++;
        }
        Assert.That(idx, Is.EqualTo(deduped.Count));
    }

    [Test]
    public void Duplicate_Keys_LastWriteWins()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            builder.Add("key"u8, "value1"u8);
            builder.Add("key"u8, "value2"u8);
        });

        Hsst.Hsst hsst = new(data);
        Assert.That(hsst.EntryCount, Is.EqualTo(2));
    }

    [Test]
    public void NestedHsst_RoundTrip()
    {
        byte[] innerData = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            builder.Add([0x01, 0x02], [0xAA, 0xBB]);
        });

        byte[] outerData = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            builder.Add([0x00], innerData);
        });

        Hsst.Hsst outer = new(outerData);
        Assert.That(outer.EntryCount, Is.EqualTo(1));
        Assert.That(outer.TryGet([0x00], out ReadOnlySpan<byte> columnData), Is.True);
        Assert.That(columnData.ToArray(), Is.EqualTo(innerData));

        Hsst.Hsst inner = new(columnData);
        Assert.That(inner.EntryCount, Is.EqualTo(1));
        Assert.That(inner.TryGet([0x01, 0x02], out ReadOnlySpan<byte> value), Is.True);
        Assert.That(value.ToArray(), Is.EqualTo(new byte[] { 0xAA, 0xBB }));
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

        byte[] accountsInner = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
        {
            builder.Add(addr, accountRlp);
        });

        byte[] emptyInner = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) => { });

        byte[] outerData = HsstTestUtil.BuildToArray((ref HsstBuilder<SpanBufferWriter> builder) =>
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

        Hsst.Hsst outer = new(outerData);
        Assert.That(outer.EntryCount, Is.EqualTo(9));

        Assert.That(outer.TryGet([0x00], out ReadOnlySpan<byte> columnData), Is.True);
        Assert.That(columnData.Length, Is.EqualTo(accountsInner.Length));
        Assert.That(columnData.ToArray(), Is.EqualTo(accountsInner));

        Hsst.Hsst inner = new(columnData);
        Assert.That(inner.EntryCount, Is.EqualTo(1));
        Assert.That(inner.TryGet(addr, out ReadOnlySpan<byte> value), Is.True);
        Assert.That(value.ToArray(), Is.EqualTo(accountRlp));
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
    public void NestedBuilder_TwoLevel_RoundTrips()
    {
        // Outer HSST with one entry whose value is an inner HSST
        byte[] buffer = new byte[4096];
        SpanBufferWriter writer = new(buffer);
        HsstBuilder<SpanBufferWriter> outer = new(ref writer);
        try
        {
            ref SpanBufferWriter innerWriter = ref outer.BeginValueWrite();
            using HsstBuilder<SpanBufferWriter> inner = new(ref innerWriter);
            inner.Add("key1"u8, "val1"u8);
            inner.Add("key2"u8, "val2"u8);
            inner.Build();
            outer.FinishValueWrite("tag"u8);
            outer.Build();
        }
        finally
        {
            outer.Dispose();
        }
        int len = writer.Written;

        Hsst.Hsst outerHsst = new(buffer.AsSpan(0, len));
        Assert.That(outerHsst.EntryCount, Is.EqualTo(1));
        Assert.That(outerHsst.TryGet("tag"u8, out ReadOnlySpan<byte> innerData), Is.True);
        Hsst.Hsst innerHsst = new(innerData);
        Assert.That(innerHsst.EntryCount, Is.EqualTo(2));
        Assert.That(innerHsst.TryGet("key1"u8, out ReadOnlySpan<byte> v1), Is.True);
        Assert.That(v1.ToArray(), Is.EqualTo("val1"u8.ToArray()));
    }

    [Test]
    public void NestedBuilder_MultipleColumns_SharedWriter_RoundTrips()
    {
        // Outer HSST with 3 columns, each an inner HSST built via shared writer
        byte[] buffer = new byte[65536];
        SpanBufferWriter writer = new(buffer);
        HsstBuilder<SpanBufferWriter> outer = new(ref writer);
        try
        {
            {
                ref SpanBufferWriter iw = ref outer.BeginValueWrite();
                using HsstBuilder<SpanBufferWriter> inner = new(ref iw);
                inner.Add("from"u8, "block0"u8);
                inner.Add("to"u8, "block1"u8);
                inner.Build();
                outer.FinishValueWrite([0x00]);
            }
            {
                ref SpanBufferWriter iw = ref outer.BeginValueWrite();
                using HsstBuilder<SpanBufferWriter> inner = new(ref iw);
                byte[] addr = new byte[20]; addr[0] = 0xAB;
                inner.Add(addr, [0xC0, 0x80]);
                inner.Build();
                outer.FinishValueWrite([0x01]);
            }
            {
                ref SpanBufferWriter iw = ref outer.BeginValueWrite();
                using HsstBuilder<SpanBufferWriter> inner = new(ref iw);
                inner.Build();
                outer.FinishValueWrite([0x02]);
            }
            outer.Build();
        }
        finally { outer.Dispose(); }
        int len = writer.Written;

        Hsst.Hsst outerHsst = new(buffer.AsSpan(0, len));
        Assert.That(outerHsst.EntryCount, Is.EqualTo(3));
        Assert.That(outerHsst.TryGet([0x00], out ReadOnlySpan<byte> col0), Is.True, "col0");
        Hsst.Hsst inner0 = new(col0);
        Assert.That(inner0.EntryCount, Is.EqualTo(2));
        Assert.That(inner0.TryGet("from"u8, out ReadOnlySpan<byte> fromVal), Is.True);
        Assert.That(fromVal.ToArray(), Is.EqualTo("block0"u8.ToArray()));
        Assert.That(outerHsst.TryGet([0x01], out ReadOnlySpan<byte> col1), Is.True, "col1");
        Assert.That(outerHsst.TryGet([0x02], out ReadOnlySpan<byte> col2), Is.True, "col2");
    }
}
