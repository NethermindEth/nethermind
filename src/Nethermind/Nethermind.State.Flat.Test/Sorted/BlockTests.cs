// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.Test.Io;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sorted;

[TestFixture]
public class BlockTests
{
    private static byte[] BuildBlock(int restartInterval, (byte[] Key, byte[] Value)[] entries)
    {
        using PooledByteBufferWriter pooled = new(256);
        using BlockBuilder block = new(restartInterval);
        foreach ((byte[] key, byte[] value) in entries)
            block.Add(key, value);
        block.Finish(ref pooled.GetWriter(), Block.FlagBlock);
        return pooled.WrittenSpan.ToArray();
    }

    private static bool SeekCeiling(byte[] block, ReadOnlySpan<byte> target, out byte[] key, out byte[] value)
    {
        SpanByteReader reader = new(block);
        Span<byte> keyBuf = stackalloc byte[256];
        if (!DataBlockReader.SeekCeiling<SpanByteReader, NoOpPin>(in reader, 0, target, keyBuf, out int keyLen, out Bound v))
        {
            key = [];
            value = [];
            return false;
        }
        key = keyBuf[..keyLen].ToArray();
        value = new byte[v.Length];
        reader.TryRead(v.Offset, value);
        return true;
    }

    [Test]
    public void Data_block_uses_2_byte_offsets()
    {
        (byte[], byte[])[] entries =
        [
            (Bytes.FromHexString("10"), Bytes.FromHexString("aa")),
            (Bytes.FromHexString("20"), Bytes.FromHexString("bb")),
            (Bytes.FromHexString("30"), Bytes.FromHexString("cc")),
        ];
        byte[] block = BuildBlock(8, entries);
        Assert.That(block[0], Is.EqualTo(Block.FlagBlock));

        foreach ((byte[] key, byte[] value) in entries)
        {
            Assert.That(SeekCeiling(block, key, out byte[] gotKey, out byte[] gotVal), Is.True);
            Assert.That(gotKey, Is.EqualTo(key));
            Assert.That(gotVal, Is.EqualTo(value));
        }
    }

    // The Index block carries 4-byte offsets and a u32 records-end, so it can span past 64 KiB — the
    // path the multi-MB index block takes for a full-state snapshot. A >64 KiB delta-coded block forces
    // recordsEnd and the restart offsets above the u16 range, exercising the full 4-byte read path.
    [Test]
    public void Index_block_round_trips_past_64KiB()
    {
        const int restartInterval = 8;
        const int count = 12000;
        (byte[] Key, long Value)[] entries = new (byte[], long)[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            entries[i] = (key, (long)i * 4096);
        }
        byte[] block = BuildDeltaBlock(restartInterval, entries);
        Assert.That(block[0], Is.EqualTo(Block.FlagIndex), "the Index flag selects 4-byte offsets");
        Assert.That(block.Length, Is.GreaterThan(ushort.MaxValue), "block must exceed 64 KiB to exercise the 4-byte path");

        foreach (int i in (int[])[0, 1, 100, 4000, 11999])
        {
            Assert.That(SeekCeilingDelta(block, entries[i].Key, out byte[] gotKey, out long gotVal), Is.True);
            Assert.That(gotKey, Is.EqualTo(entries[i].Key));
            Assert.That(gotVal, Is.EqualTo(entries[i].Value), $"absolute offset for entry {i}");
        }

        byte[] pastEnd = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(pastEnd, count);
        Assert.That(SeekCeilingDelta(block, pastEnd, out _, out _), Is.False);
    }

    [Test]
    public void Ceiling_before_first_key_returns_first()
    {
        byte[] block = BuildBlock(8,
        [
            (Bytes.FromHexString("10"), Bytes.FromHexString("a0")),
            (Bytes.FromHexString("20"), Bytes.FromHexString("a1")),
            (Bytes.FromHexString("30"), Bytes.FromHexString("a2")),
        ]);
        Assert.That(SeekCeiling(block, Bytes.FromHexString("05"), out byte[] key, out byte[] value), Is.True);
        Assert.That(key, Is.EqualTo(Bytes.FromHexString("10")));
        Assert.That(value, Is.EqualTo(Bytes.FromHexString("a0")));
    }

    // 9 records at interval 8 ⇒ two restart runs (records 0..7, then record 8). A target between the
    // last key of run 0 and the first key of run 1 must scan ACROSS the restart boundary — guards the
    // "scan to recordsEnd, not runEnd" rule.
    [Test]
    public void Ceiling_in_gap_scans_across_restart_runs()
    {
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[9];
        for (int i = 0; i < 8; i++) entries[i] = ([(byte)i], [(byte)i]);
        entries[8] = (Bytes.FromHexString("10"), Bytes.FromHexString("ff")); // first key of restart run 1

        byte[] block = BuildBlock(8, entries);
        Assert.That(SeekCeiling(block, Bytes.FromHexString("0a"), out byte[] key, out byte[] value), Is.True);
        Assert.That(key, Is.EqualTo(Bytes.FromHexString("10")));
        Assert.That(value, Is.EqualTo(Bytes.FromHexString("ff")));
    }

    [Test]
    public void Ceiling_past_last_key_returns_false()
    {
        byte[] block = BuildBlock(8,
        [
            (Bytes.FromHexString("10"), Bytes.FromHexString("a0")),
            (Bytes.FromHexString("20"), Bytes.FromHexString("a1")),
        ]);
        Assert.That(SeekCeiling(block, Bytes.FromHexString("30"), out _, out _), Is.False);
    }

    [Test]
    public void Ceiling_on_empty_block_returns_false()
    {
        byte[] block = BuildBlock(8, []);
        Assert.That(SeekCeiling(block, Bytes.FromHexString("00"), out _, out _), Is.False);
    }

    private static byte[] BuildDeltaBlock(int restartInterval, (byte[] Key, long Value)[] entries)
    {
        using PooledByteBufferWriter pooled = new(256);
        using BlockBuilder block = new(restartInterval);
        foreach ((byte[] key, long value) in entries)
            block.AddDeltaValue(key, value);
        // Delta values are the index block's encoding, so finish under the Index role flag.
        block.Finish(ref pooled.GetWriter(), Block.FlagIndex);
        return pooled.WrittenSpan.ToArray();
    }

    private static bool SeekCeilingDelta(byte[] block, ReadOnlySpan<byte> target, out byte[] key, out long value)
    {
        SpanByteReader reader = new(block);
        Span<byte> keyBuf = stackalloc byte[256];
        if (!IndexBlockReader.SeekCeiling<SpanByteReader, NoOpPin>(in reader, 0, target, keyBuf, out int keyLen, out long v))
        {
            key = [];
            value = 0;
            return false;
        }
        key = keyBuf[..keyLen].ToArray();
        value = v;
        return true;
    }

    // Delta-coded index values: 12 ascending offsets over 3 restart runs. Keys share a leading 0x01 byte
    // so only the forced restarts (every 4 records, heads at index 0, 4, 8) have cp == 0 and re-anchor to
    // an absolute value; the in-between records have cp == 1 and store a delta. Reconstruction must hit the
    // right absolute offset for a restart head (incl. the zero/vs=0 head), a mid-run delta record, a gap
    // probe whose ceiling is the next run's head (the crossing record must re-anchor as an absolute, not
    // accumulate a stale delta), the before-first case, and a past-end miss.
    [Test]
    public void Delta_value_seek_reconstructs_absolute_offsets()
    {
        const int restartInterval = 4;
        (byte[] Key, long Value)[] entries =
        [
            (Bytes.FromHexString("0102"), 0),            // restart head (cp == 0), vs = 0 (zero bytes)
            (Bytes.FromHexString("0104"), 4096),         // cp == 1 ⇒ delta 4096
            (Bytes.FromHexString("0106"), 8192),
            (Bytes.FromHexString("0108"), 12288),
            (Bytes.FromHexString("010a"), 1_000_000),    // restart head (3-byte absolute)
            (Bytes.FromHexString("010c"), 1_004_096),
            (Bytes.FromHexString("010e"), 1_008_192),
            (Bytes.FromHexString("0110"), 1_012_288),
            (Bytes.FromHexString("0112"), 250_000_000),  // restart head (4-byte absolute)
            (Bytes.FromHexString("0114"), 250_004_096),
            (Bytes.FromHexString("0116"), 250_008_192),
            (Bytes.FromHexString("0118"), 250_012_288),
        ];
        byte[] block = BuildDeltaBlock(restartInterval, entries);

        foreach ((byte[] key, long value) in entries)
        {
            Assert.That(SeekCeilingDelta(block, key, out byte[] gotKey, out long gotVal), Is.True);
            Assert.That(gotKey, Is.EqualTo(key));
            Assert.That(gotVal, Is.EqualTo(value), $"absolute offset for key {key.ToHexString()}");
        }

        // Target before the first key ⇒ ceiling is the head record (offset 0).
        Assert.That(SeekCeilingDelta(block, Bytes.FromHexString("0100"), out _, out long beforeFirst), Is.True);
        Assert.That(beforeFirst, Is.EqualTo(0));

        // "0109" sits between run 0's tail (0108) and run 1's head (010a): the ceiling is the next run's
        // head, whose value must re-anchor to the absolute 1_000_000.
        Assert.That(SeekCeilingDelta(block, Bytes.FromHexString("0109"), out byte[] crossKey, out long crossVal), Is.True);
        Assert.That(crossKey, Is.EqualTo(Bytes.FromHexString("010a")));
        Assert.That(crossVal, Is.EqualTo(1_000_000));

        Assert.That(SeekCeilingDelta(block, Bytes.FromHexString("0119"), out _, out _), Is.False);
    }
}
