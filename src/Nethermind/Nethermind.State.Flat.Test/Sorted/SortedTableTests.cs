// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sorted;

[TestFixture]
public class SortedTableTests
{
    // Mixed key lengths, a prefix pair ("00" / "0000"), and an empty value.
    private static (byte[] Key, byte[] Value)[] SampleEntries() =>
    [
        (Bytes.FromHexString("00"), Bytes.FromHexString("aa")),
        (Bytes.FromHexString("0000"), []),
        (Bytes.FromHexString("01ff"), Bytes.FromHexString("0102030405")),
        (Bytes.FromHexString("7f"), Bytes.FromHexString("01")),
        (Bytes.FromHexString("fe00112233"), Bytes.FromHexString("99")),
        (Bytes.FromHexString("ff"), Bytes.FromHexString("deadbeef")),
    ];

    private static byte[] BuildTable((byte[] Key, byte[] Value)[] entries, int[] insertionOrder)
    {
        using PooledByteBufferWriter pooled = new(256);
        SortedTableBuilder<PooledByteBufferWriter.Writer> table = new(ref pooled.GetWriter(), entries.Length);
        try
        {
            foreach (int i in insertionOrder)
                table.Add(entries[i].Key, entries[i].Value);
            table.Build();
        }
        finally
        {
            table.Dispose();
        }
        return pooled.WrittenSpan.ToArray();
    }

    [Test]
    public void Round_trips_every_key_and_reports_misses()
    {
        (byte[] Key, byte[] Value)[] entries = SampleEntries();
        // Insert out of sorted order to prove Build sorts.
        byte[] bytes = BuildTable(entries, [5, 0, 3, 1, 4, 2]);

        SpanByteReader reader = new(bytes);
        Bound table = new(0, reader.Length);

        foreach ((byte[] key, byte[] value) in entries)
        {
            Assert.That(SortedTableReader.TrySeek<SpanByteReader, NoOpPin>(in reader, table, key, out Bound v),
                Is.True, $"key {key.ToHexString()} should be found");
            byte[] got = new byte[v.Length];
            reader.TryRead(v.Offset, got);
            Assert.That(got, Is.EqualTo(value), $"value for {key.ToHexString()}");
        }

        // Misses: an absent key, and a key that is a prefix of a present one but not itself present.
        Assert.That(SortedTableReader.TrySeek<SpanByteReader, NoOpPin>(in reader, table, Bytes.FromHexString("02"), out _), Is.False);
        Assert.That(SortedTableReader.TrySeek<SpanByteReader, NoOpPin>(in reader, table, Bytes.FromHexString("0001"), out _), Is.False);
        Assert.That(SortedTableReader.TrySeek<SpanByteReader, NoOpPin>(in reader, table, Bytes.FromHexString("ffff"), out _), Is.False);
    }

    [Test]
    public void Enumerates_in_ascending_key_order()
    {
        (byte[] Key, byte[] Value)[] entries = SampleEntries();
        byte[] bytes = BuildTable(entries, [.. Enumerable.Range(0, entries.Length).Reverse()]);

        SpanByteReader reader = new(bytes);
        SortedTableEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, reader.Length));
        List<byte[]> keys = [];
        while (e.MoveNext(in reader)) keys.Add(e.CurrentKey.ToArray());

        Assert.That(keys.Count, Is.EqualTo(entries.Length));
        for (int i = 1; i < keys.Count; i++)
            Assert.That(keys[i - 1].AsSpan().SequenceCompareTo(keys[i]), Is.LessThan(0), "keys must be strictly ascending");
    }

    [Test]
    public void Empty_table_seek_returns_false()
    {
        byte[] bytes = BuildTable([], []);
        SpanByteReader reader = new(bytes);
        Assert.That(SortedTableReader.TrySeek<SpanByteReader, NoOpPin>(
            in reader, new Bound(0, reader.Length), Bytes.FromHexString("00"), out _), Is.False);
    }

    // Exercise the sparse index across last-block sizes 1..8 (partial and full final blocks).
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(16)]
    [TestCase(17)]
    public void Round_trips_across_block_boundaries(int count)
    {
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            entries[i] = (key, [(byte)i]);
        }
        int[] order = [.. Enumerable.Range(0, count).Reverse()];
        byte[] bytes = BuildTable(entries, order);

        SpanByteReader reader = new(bytes);
        Bound table = new(0, reader.Length);
        for (int i = 0; i < count; i++)
        {
            Assert.That(SortedTableReader.TrySeek<SpanByteReader, NoOpPin>(in reader, table, entries[i].Key, out Bound v), Is.True);
            byte[] got = new byte[v.Length];
            reader.TryRead(v.Offset, got);
            Assert.That(got, Is.EqualTo(entries[i].Value));
        }
        byte[] missing = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(missing, count);
        Assert.That(SortedTableReader.TrySeek<SpanByteReader, NoOpPin>(in reader, table, missing, out _), Is.False);
    }

    [Test]
    public void Large_table_round_trips_after_buffer_growth()
    {
        // Enough entries to force the builder's key/entry buffers to grow several times.
        const int count = 5000;
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            entries[i] = (key, [(byte)(i & 0xFF), (byte)((i >> 8) & 0xFF)]);
        }
        // Insertion order: a deterministic shuffle (stride coprime to count).
        int[] order = new int[count];
        for (int i = 0; i < count; i++) order[i] = (int)((long)i * 2654435761L % count);
        // Ensure the shuffle is a permutation; fall back to identity for any unlikely collision.
        if (order.Distinct().Count() != count)
            for (int i = 0; i < count; i++) order[i] = i;

        byte[] bytes = BuildTable(entries, order);
        SpanByteReader reader = new(bytes);
        Bound table = new(0, reader.Length);

        for (int i = 0; i < count; i++)
        {
            Assert.That(SortedTableReader.TrySeek<SpanByteReader, NoOpPin>(in reader, table, entries[i].Key, out Bound v), Is.True);
            byte[] got = new byte[v.Length];
            reader.TryRead(v.Offset, got);
            Assert.That(got, Is.EqualTo(entries[i].Value));
        }

        byte[] missing = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(missing, count + 1);
        Assert.That(SortedTableReader.TrySeek<SpanByteReader, NoOpPin>(in reader, table, missing, out _), Is.False);
    }
}
