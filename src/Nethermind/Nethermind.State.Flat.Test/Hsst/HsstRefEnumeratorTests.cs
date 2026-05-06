// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class HsstRefEnumeratorTests
{
    [Test]
    public void Enumerate_Empty_ReturnsNothing()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) => { });
        SpanByteReader reader = new(data);
        using HsstRefEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length));
        Assert.That(e.MoveNext(), Is.False);
    }

    [Test]
    public void Enumerate_SingleEntry_YieldsOnce()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
            builder.Add("key1"u8, "value1"u8));
        SpanByteReader reader = new(data);
        using HsstRefEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length));

        Assert.That(e.MoveNext(), Is.True);
        Bound k = e.Current.KeyBound;
        Assert.That(Encoding.UTF8.GetString(data.AsSpan((int)k.Offset, (int)k.Length)), Is.EqualTo("key1"));
        Bound v = e.Current.ValueBound;
        Assert.That(Encoding.UTF8.GetString(data.AsSpan((int)v.Offset, (int)v.Length)), Is.EqualTo("value1"));
        Assert.That(e.MoveNext(), Is.False);
    }

    [TestCase(2)]
    [TestCase(10)]
    [TestCase(64)]
    [TestCase(65)] // forces multi-level B-tree
    [TestCase(200)]
    [TestCase(1000)]
    [TestCase(5000)]
    public void Enumerate_YieldsAllEntries_InSortedOrder(int count)
    {
        List<(string Key, string Value)> entries = new();
        for (int i = 0; i < count; i++)
            entries.Add(($"key_{i:D6}", $"val_{i:D6}"));

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            foreach ((string key, string value) in entries)
                builder.Add(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
        });
        entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

        SpanByteReader reader = new(data);
        using HsstRefEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length));

        int idx = 0;
        while (e.MoveNext())
        {
            (string expectedKey, string expectedValue) = entries[idx];
            Bound k = e.Current.KeyBound;
            Assert.That(Encoding.UTF8.GetString(data.AsSpan((int)k.Offset, (int)k.Length)), Is.EqualTo(expectedKey),
                $"Key mismatch at idx {idx}");
            Bound v = e.Current.ValueBound;
            Assert.That(Encoding.UTF8.GetString(data.AsSpan((int)v.Offset, (int)v.Length)), Is.EqualTo(expectedValue),
                $"Value mismatch at idx {idx}");
            idx++;
        }
        Assert.That(idx, Is.EqualTo(count));
    }

    [TestCase(100, 4, 32, 32, 42)]
    [TestCase(500, 8, 64, 128, 101)]
    [TestCase(1000, 64, 64, 128, 202)]
    public void Enumerate_BinaryKeys_VariableSize(int count, int maxLeafEntries, int maxKeyLen, int maxValLen, int seed)
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
        using HsstRefEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length));

        int idx = 0;
        while (e.MoveNext())
        {
            Bound k = e.Current.KeyBound;
            Assert.That(data.AsSpan((int)k.Offset, (int)k.Length).SequenceEqual(deduped[idx].Key), Is.True,
                $"Key mismatch at idx {idx}");
            Bound v = e.Current.ValueBound;
            Assert.That(data.AsSpan((int)v.Offset, (int)v.Length).SequenceEqual(deduped[idx].Value), Is.True,
                $"Value mismatch at idx {idx}");
            idx++;
        }
        Assert.That(idx, Is.EqualTo(deduped.Count));
    }

    [Test]
    public void Enumerate_NestedHsst_OuterAndInner()
    {
        // Outer keyed by addr; each value is an inner HSST keyed by subtag.
        byte[] inner1 = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            builder.Add("subtag1"u8, "v1"u8);
            builder.Add("subtag2"u8, "v2"u8);
        });
        byte[] inner2 = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
            builder.Add("subtag1"u8, "x1"u8));

        byte[] outer = HsstTestUtil.BuildToArray((ref HsstBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            builder.Add("addr1"u8, inner1);
            builder.Add("addr2"u8, inner2);
        });

        SpanByteReader reader = new(outer);
        using HsstRefEnumerator<SpanByteReader, NoOpPin> outerEnum = new(in reader, new Bound(0, outer.Length));

        List<string> seenAddrs = [];
        Dictionary<string, List<string>> seenSubtags = [];
        while (outerEnum.MoveNext())
        {
            Bound ak = outerEnum.Current.KeyBound;
            string addr = Encoding.UTF8.GetString(outer.AsSpan((int)ak.Offset, (int)ak.Length));
            seenAddrs.Add(addr);
            List<string> subs = [];

            using HsstRefEnumerator<SpanByteReader, NoOpPin> innerEnum = new(in reader, outerEnum.Current.ValueBound);
            while (innerEnum.MoveNext())
            {
                Bound sk = innerEnum.Current.KeyBound;
                string sub = Encoding.UTF8.GetString(outer.AsSpan((int)sk.Offset, (int)sk.Length));
                Bound v = innerEnum.Current.ValueBound;
                string val = Encoding.UTF8.GetString(outer.AsSpan((int)v.Offset, (int)v.Length));
                subs.Add($"{sub}={val}");
            }
            seenSubtags[addr] = subs;
        }

        Assert.That(seenAddrs, Is.EqualTo(new[] { "addr1", "addr2" }));
        Assert.That(seenSubtags["addr1"], Is.EqualTo(new[] { "subtag1=v1", "subtag2=v2" }));
        Assert.That(seenSubtags["addr2"], Is.EqualTo(new[] { "subtag1=x1" }));
    }

}
