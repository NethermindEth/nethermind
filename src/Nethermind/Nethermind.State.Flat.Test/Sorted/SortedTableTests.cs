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

    private static int BlockCount(byte[] bytes)
    {
        SpanByteReader reader = new(bytes);
        Assert.That(SortedTable.TryReadFooter<SpanByteReader, NoOpPin>(in reader, new Bound(0, reader.Length), out SortedTable.Footer footer), Is.True);
        return footer.NumBlocks;
    }

    private static bool Seek(byte[] bytes, ReadOnlySpan<byte> key, out byte[] value)
    {
        SpanByteReader reader = new(bytes);
        if (!SortedTableReader.TrySeek<SpanByteReader, NoOpPin>(in reader, new Bound(0, reader.Length), key, out Bound v))
        {
            value = [];
            return false;
        }
        value = new byte[v.Length];
        reader.TryRead(v.Offset, value);
        return true;
    }

    private static List<byte[]> Enumerate(byte[] bytes)
    {
        SpanByteReader reader = new(bytes);
        SortedTableEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, reader.Length));
        List<byte[]> keys = [];
        while (e.MoveNext(in reader)) keys.Add(e.CurrentKey.ToArray());
        return keys;
    }

    [Test]
    public void Round_trips_every_key_and_reports_misses()
    {
        (byte[] Key, byte[] Value)[] entries = SampleEntries();
        // Insert out of sorted order to prove Build sorts.
        byte[] bytes = BuildTable(entries, [5, 0, 3, 1, 4, 2]);

        foreach ((byte[] key, byte[] value) in entries)
        {
            Assert.That(Seek(bytes, key, out byte[] got), Is.True, $"key {key.ToHexString()} should be found");
            Assert.That(got, Is.EqualTo(value), $"value for {key.ToHexString()}");
        }

        // Misses: an absent key, and a key that is a prefix of a present one but not itself present.
        Assert.That(Seek(bytes, Bytes.FromHexString("02"), out _), Is.False);
        Assert.That(Seek(bytes, Bytes.FromHexString("0001"), out _), Is.False);
        Assert.That(Seek(bytes, Bytes.FromHexString("ffff"), out _), Is.False);
    }

    [Test]
    public void Enumerates_in_ascending_key_order()
    {
        (byte[] Key, byte[] Value)[] entries = SampleEntries();
        byte[] bytes = BuildTable(entries, [.. Enumerable.Range(0, entries.Length).Reverse()]);

        List<byte[]> keys = Enumerate(bytes);
        Assert.That(keys.Count, Is.EqualTo(entries.Length));
        for (int i = 1; i < keys.Count; i++)
            Assert.That(keys[i - 1].AsSpan().SequenceCompareTo(keys[i]), Is.LessThan(0), "keys must be strictly ascending");
    }

    [Test]
    public void Empty_table_seeks_and_enumerates_nothing()
    {
        byte[] bytes = BuildTable([], []);
        Assert.That(BlockCount(bytes), Is.EqualTo(0));
        Assert.That(Seek(bytes, Bytes.FromHexString("00"), out _), Is.False);
        Assert.That(Enumerate(bytes), Is.Empty);
    }

    [Test]
    public void Single_record_round_trips()
    {
        (byte[] Key, byte[] Value)[] entries = [(Bytes.FromHexString("abcdef"), Bytes.FromHexString("1234"))];
        byte[] bytes = BuildTable(entries, [0]);

        Assert.That(BlockCount(bytes), Is.EqualTo(1));
        Assert.That(Seek(bytes, entries[0].Key, out byte[] got), Is.True);
        Assert.That(got, Is.EqualTo(entries[0].Value));
        Assert.That(Seek(bytes, Bytes.FromHexString("abcdee"), out _), Is.False); // before
        Assert.That(Seek(bytes, Bytes.FromHexString("abcdff"), out _), Is.False); // after
        Assert.That(Enumerate(bytes).Count, Is.EqualTo(1));
    }

    // A single 4 KB block, exercising restart-run boundaries around RestartInterval (= 8): the
    // builder resets front-coding every restart, the reader binary-searches restarts then scans one run.
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(16)]
    [TestCase(24)]
    [TestCase(25)]
    [TestCase(48)]
    public void Restart_boundaries_within_one_block(int count)
    {
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            entries[i] = (key, [(byte)i, (byte)(i + 1)]);
        }
        byte[] bytes = BuildTable(entries, [.. Enumerable.Range(0, count).Reverse()]);

        Assert.That(BlockCount(bytes), Is.EqualTo(1), "small values keep all records in one block");
        for (int i = 0; i < count; i++)
        {
            Assert.That(Seek(bytes, entries[i].Key, out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(entries[i].Value));
        }
        byte[] missing = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(missing, count);
        Assert.That(Seek(bytes, missing, out _), Is.False);
    }

    // Exercise the last-block fill across single-block sizes 1..17.
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(16)]
    [TestCase(17)]
    public void Round_trips_across_record_counts(int count)
    {
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            entries[i] = (key, [(byte)i]);
        }
        byte[] bytes = BuildTable(entries, [.. Enumerable.Range(0, count).Reverse()]);

        for (int i = 0; i < count; i++)
        {
            Assert.That(Seek(bytes, entries[i].Key, out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(entries[i].Value));
        }
        byte[] missing = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(missing, count);
        Assert.That(Seek(bytes, missing, out _), Is.False);
    }

    // Large values force many 4 KB blocks. Present keys are odd, so every even probe lands in a gap —
    // including gaps that straddle a block boundary (the separator lower-bound + in-block re-validation),
    // plus the before-first and after-last sentinels.
    [TestCase(50)]
    [TestCase(800)]
    [TestCase(4000)]
    public void Round_trips_multiblock_with_gaps(int count)
    {
        byte[] value = new byte[200];
        for (int i = 0; i < value.Length; i++) value[i] = (byte)i;
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(key, 2 * i + 1); // odd
            entries[i] = (key, value);
        }
        byte[] bytes = BuildTable(entries, [.. Enumerable.Range(0, count).Reverse()]);

        Assert.That(BlockCount(bytes), Is.GreaterThan(1), "200-byte values span multiple 4 KB blocks");

        for (int i = 0; i < count; i++)
        {
            Assert.That(Seek(bytes, entries[i].Key, out byte[] got), Is.True, $"present key #{i}");
            Assert.That(got, Is.EqualTo(value));

            byte[] gap = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(gap, 2 * i); // even: before-first (i==0) or between two present keys
            Assert.That(Seek(bytes, gap, out _), Is.False, $"gap key {2 * i}");
        }
        byte[] after = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(after, 2 * count); // > last present key
        Assert.That(Seek(bytes, after, out _), Is.False);

        List<byte[]> keys = Enumerate(bytes);
        Assert.That(keys.Count, Is.EqualTo(count));
        for (int i = 1; i < keys.Count; i++)
            Assert.That(keys[i - 1].AsSpan().SequenceCompareTo(keys[i]), Is.LessThan(0), "ascending across every block boundary");
    }

    // 32-byte keys sharing a 30-byte prefix, differing only in the last two bytes — exercises long
    // front-coded cp within restart runs and the cp == 0 reset at each restart and block boundary.
    [TestCase(20)]
    [TestCase(4000)]
    public void Long_shared_prefix_round_trips(int count)
    {
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[32];
            key.AsSpan(0, 30).Fill(0xAB);
            BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(30), (ushort)i);
            entries[i] = (key, [(byte)i, (byte)(i + 1)]);
        }
        byte[] bytes = BuildTable(entries, [.. Enumerable.Range(0, count).Reverse()]);

        for (int i = 0; i < count; i++)
        {
            Assert.That(Seek(bytes, entries[i].Key, out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(entries[i].Value));
        }

        // Enumeration reconstructs the full 32-byte keys in ascending order.
        List<byte[]> keys = Enumerate(bytes);
        Assert.That(keys.Count, Is.EqualTo(count));
        for (int i = 0; i < count; i++)
        {
            Assert.That(keys[i].Length, Is.EqualTo(32));
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(keys[i].AsSpan(30)), Is.EqualTo((ushort)i));
        }

        byte[] missing = new byte[32];
        missing.AsSpan(0, 30).Fill(0xAB);
        BinaryPrimitives.WriteUInt16BigEndian(missing.AsSpan(30), (ushort)count);
        Assert.That(Seek(bytes, missing, out _), Is.False);
    }

    // Fuzz arbitrary block fills, restart placements, separator computation and front-coding across
    // boundaries with random unique keys (1..55 B) and values (0..254 B).
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(42)]
    public void Fuzz_round_trips_random_tables(int seed)
    {
        Random rng = new(seed);
        for (int iter = 0; iter < 25; iter++)
        {
            int count = rng.Next(1, 1500);
            Dictionary<string, byte[]> map = new(count);
            while (map.Count < count)
            {
                byte[] key = new byte[rng.Next(1, 56)];
                rng.NextBytes(key);
                byte[] value = new byte[rng.Next(0, 255)];
                rng.NextBytes(value);
                map[key.ToHexString()] = value;
            }

            (byte[] Key, byte[] Value)[] entries = [.. map.Select(kv => (Bytes.FromHexString(kv.Key), kv.Value))];
            byte[] bytes = BuildTable(entries, [.. Enumerable.Range(0, entries.Length).Reverse()]);

            foreach ((byte[] key, byte[] value) in entries)
            {
                Assert.That(Seek(bytes, key, out byte[] got), Is.True);
                Assert.That(got, Is.EqualTo(value));
            }

            // Random probes; most are absent. Compare against the source map for the verdict.
            for (int p = 0; p < 50; p++)
            {
                byte[] probe = new byte[rng.Next(1, 56)];
                rng.NextBytes(probe);
                bool present = map.TryGetValue(probe.ToHexString(), out byte[]? expected);
                Assert.That(Seek(bytes, probe, out byte[] got), Is.EqualTo(present));
                if (present) Assert.That(got, Is.EqualTo(expected));
            }

            List<byte[]> keys = Enumerate(bytes);
            Assert.That(keys.Count, Is.EqualTo(entries.Length));
            for (int i = 1; i < keys.Count; i++)
                Assert.That(keys[i - 1].AsSpan().SequenceCompareTo(keys[i]), Is.LessThan(0));
        }
    }

    // Every data block is zero-padded to BlockSize, so block i starts at i*BlockSize and the index
    // block starts at M*BlockSize — both must parse as valid self-describing blocks.
    [Test]
    public void Data_blocks_are_4k_aligned_and_index_follows()
    {
        const int count = 300;
        byte[] value = new byte[200];
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            entries[i] = (key, value);
        }
        byte[] bytes = BuildTable(entries, [.. Enumerable.Range(0, count).Reverse()]);

        SpanByteReader reader = new(bytes);
        Bound table = new(0, reader.Length);
        Assert.That(SortedTable.TryReadFooter<SpanByteReader, NoOpPin>(in reader, table, out SortedTable.Footer footer), Is.True);
        int m = footer.NumBlocks;
        Assert.That(m, Is.GreaterThan(1));

        for (int i = 0; i < m; i++)
            Assert.That(BlockReader.ReadHeader<SpanByteReader, NoOpPin>(in reader, (long)i * SortedTable.BlockSize, out int w, out _, out _, out _) && (w is Block.Width2 or Block.Width4),
                Is.True, $"data block {i} at {i * SortedTable.BlockSize}");
        // The index block sits right after the last (unpadded) data block.
        Assert.That(BlockReader.ReadHeader<SpanByteReader, NoOpPin>(in reader, SortedTable.IndexBlockStart(table, footer), out _, out _, out _, out _), Is.True, "index block after the last data block");
    }

    // u32 block number * 4 KiB reaches ~16 TiB; the helper must widen before multiplying.
    [Test]
    public void Block_number_addressing_does_not_overflow() =>
        Assert.That(SortedTable.DataBlockStart(new Bound(0, 0), uint.MaxValue), Is.EqualTo((long)uint.MaxValue * SortedTable.BlockSize));

    [Test]
    public void Large_table_round_trips_after_buffer_growth()
    {
        // Enough entries to force the builder's key/entry buffers to grow several times and span blocks.
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
        Assert.That(BlockCount(bytes), Is.GreaterThan(1));

        for (int i = 0; i < count; i++)
        {
            Assert.That(Seek(bytes, entries[i].Key, out byte[] got), Is.True);
            Assert.That(got, Is.EqualTo(entries[i].Value));
        }

        byte[] missing = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(missing, count + 1);
        Assert.That(Seek(bytes, missing, out _), Is.False);
    }
}
