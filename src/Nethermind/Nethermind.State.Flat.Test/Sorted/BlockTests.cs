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
    private static byte[] BuildBlock(int restartInterval, (byte[] Key, byte[] Value)[] entries, byte formatFlag = Block.FlagBlock)
    {
        using PooledByteBufferWriter pooled = new(256);
        using BlockBuilder block = new(restartInterval);
        foreach ((byte[] key, byte[] value) in entries)
            block.Add(key, value);
        block.Finish(ref pooled.GetWriter(), formatFlag);
        return pooled.WrittenSpan.ToArray();
    }

    private static bool SeekCeiling(byte[] block, ReadOnlySpan<byte> target, out byte[] key, out byte[] value)
    {
        SpanByteReader reader = new(block);
        Span<byte> keyBuf = stackalloc byte[256];
        if (!BlockReader.SeekCeiling<SpanByteReader, NoOpPin>(in reader, 0, target, keyBuf, out int keyLen, out Bound v))
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

    // The Index block carries 4-byte offsets so it can span past 64 KiB — the path the multi-MB index
    // block takes for a full-state snapshot, exercised cheaply here by building a >64 KiB block directly.
    [Test]
    public void Index_block_round_trips_past_64KiB()
    {
        const int count = 8000;
        (byte[] Key, byte[] Value)[] entries = new (byte[], byte[])[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            entries[i] = (key, [(byte)i, (byte)(i >> 8), 0xAB, 0xCD, 0xEF, 0x01, 0x02, 0x03]);
        }
        byte[] block = BuildBlock(8, entries, Block.FlagIndex);
        Assert.That(block[0], Is.EqualTo(Block.FlagIndex), "the Index flag selects 4-byte offsets");

        foreach (int i in (int[])[0, 1, 100, 4000, 7999])
        {
            Assert.That(SeekCeiling(block, entries[i].Key, out byte[] gotKey, out byte[] gotVal), Is.True);
            Assert.That(gotKey, Is.EqualTo(entries[i].Key));
            Assert.That(gotVal, Is.EqualTo(entries[i].Value));
        }

        byte[] pastEnd = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(pastEnd, count);
        Assert.That(SeekCeiling(block, pastEnd, out _, out _), Is.False);
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
}
