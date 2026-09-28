// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtGroupGeometryTests
{
    [Test]
    public void Layout_is_post_order_with_every_parent_after_its_children([Range(1, PbtGroupGeometry.MaxLevelsPerGroup)] int levels)
    {
        (NodeGroupPath[] localPaths, short[] parents) = PbtGroupGeometry.CreateLayout(levels);
        int slots = 1 << levels;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(localPaths, Has.Length.EqualTo(2 * slots - 1));
            for (int position = 0; position < localPaths.Length; position++)
            {
                NodeGroupPath local = localPaths[position];
                int width = slots >> local.Length;
                Assert.That(NodeGroupPath.PositionOf(local.Slot, width), Is.EqualTo(position), $"position {position}");
                Assert.That(local.Slot % width, Is.Zero, $"slot of position {position}");
                if (local.Length == 0)
                {
                    Assert.That(parents[position], Is.EqualTo(-1));
                    continue;
                }
                NodeGroupPath parent = localPaths[parents[position]];
                Assert.That(parents[position], Is.GreaterThan(position), $"parent of position {position}");
                Assert.That(parent.Length, Is.EqualTo(local.Length - 1), $"parent of position {position}");
                Assert.That(local.Slot - parent.Slot, Is.InRange(0, 2 * width - 1), $"parent of position {position}");
            }
            if (levels != 4) return;
            // The tables the layout replaced when groups were always four levels deep.
            Assert.That(parents, Is.EqualTo(new short[] { 2, 2, 6, 5, 5, 6, 14, 9, 9, 13, 12, 12, 13, 14, 30, 17, 17, 21, 20, 20, 21, 29, 24, 24, 28, 27, 27, 28, 29, 30, -1 }));
            Assert.That(Array.ConvertAll(localPaths, local => local.Slot >> (4 - local.Length)),
                Is.EqualTo(new[] { 0, 1, 0, 2, 3, 1, 0, 4, 5, 2, 6, 7, 3, 1, 0, 8, 9, 4, 10, 11, 5, 2, 12, 13, 6, 14, 15, 7, 3, 1, 0 }));
            Assert.That(Array.ConvertAll(localPaths, local => local.Length),
                Is.EqualTo(new[] { 4, 4, 3, 4, 4, 3, 2, 4, 4, 3, 4, 4, 3, 2, 1, 4, 4, 3, 4, 4, 3, 2, 4, 4, 3, 4, 4, 3, 2, 1, 0 }));
        }
    }

    [Test]
    public void Slot_reads_the_group_bits_at_every_offset_as_zeros_past_the_key_end()
    {
        byte[] key = new byte[PbtTreeKey.MaxLength];
        new Random(8297).NextBytes(key);
        // Levels that divide eight are only ever read at a multiple of themselves.
        int step = 8 % PbtGroupGeometry.LevelsPerGroup == 0 ? PbtGroupGeometry.LevelsPerGroup : 1;
        using (Assert.EnterMultipleScope())
        {
            for (int bitOffset = 0; bitOffset < key.Length * 8; bitOffset += step)
            {
                int expected = 0;
                for (int bit = bitOffset; bit < bitOffset + PbtGroupGeometry.LevelsPerGroup; bit++)
                    expected = expected << 1 | (bit < key.Length * 8 ? TrieUpdater.GetBit(key, bit) : 0);
                Assert.That(PbtGroupGeometry.ReadSlot(key, bitOffset), Is.EqualTo(expected), $"offset {bitOffset}");
            }
        }
    }

    [Test]
    public void Append_bits_matches_bit_by_bit_appending([Range(0, 8)] int bitCount)
    {
        Random random = new(bitCount);
        Span<byte> cursorBuffer = stackalloc byte[PbtStorageTreeKey.MaxLength];
        using (Assert.EnterMultipleScope())
        {
            for (int depth = 0; depth <= 24; depth++)
            {
                byte[] prefix = new byte[PbtBitPrefix.ByteCount(depth)];
                random.NextBytes(prefix);
                if ((depth & 7) != 0) prefix[^1] &= (byte)(0xFF << (8 - (depth & 7)));
                int bits = random.Next(1 << bitCount);
                byte[] expected = new byte[PbtBitPrefix.ByteCount(depth + bitCount)];
                prefix.CopyTo(expected, 0);
                for (int bit = 0; bit < bitCount; bit++)
                    if ((bits >> (bitCount - 1 - bit) & 1) != 0) expected[(depth + bit) >> 3] |= (byte)(0x80 >> ((depth + bit) & 7));

                PbtStorageNodePath appended = new PbtStorageNodePath(prefix, depth).AppendBits(bits, bitCount);
                PbtTraversalPath cursor = PbtTraversalPath.FromPath(cursorBuffer, new PbtStorageNodePath(prefix, depth));
                cursor.AppendBits(bits, bitCount);
                Assert.That(appended, Is.EqualTo(new PbtStorageNodePath(expected, depth + bitCount)), $"depth {depth}");
                Assert.That(cursor.ToPath<PbtStorageNodePath>(), Is.EqualTo(appended), $"cursor at depth {depth}");
            }
        }
    }

    [Test]
    public void Bitmap_operations_match_a_bool_reference()
    {
        int bitCount = PbtBitmap.WordCount * 64;
        Random random = new(8297);
        bool[] reference = new bool[bitCount];
        bool[] other = new bool[bitCount];
        PbtBitmap bitmap = default;
        PbtBitmap otherBitmap = default;
        for (int bit = 0; bit < bitCount; bit++)
        {
            if (random.Next(3) == 0) { reference[bit] = true; bitmap.Set(bit); }
            if (random.Next(2) == 0) { other[bit] = true; otherBitmap.Set(bit); }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bitmap.PopCount(), Is.EqualTo(Array.FindAll(reference, set => set).Length));
            for (int bit = 0; bit < bitCount; bit++)
            {
                Assert.That(bitmap.IsSet(bit), Is.EqualTo(reference[bit]), $"bit {bit}");
                Assert.That(bitmap.PopCountBelow(bit), Is.EqualTo(Array.FindAll(reference[..bit], set => set).Length), $"below {bit}");
                Assert.That(bitmap.NextSetBit(bit), Is.EqualTo(Array.IndexOf(reference, true, bit)), $"next from {bit}");
                int count = random.Next(bitCount - bit + 1);
                bool[] range = reference[bit..(bit + count)];
                Assert.That(bitmap.PopCountRange(bit, count), Is.EqualTo(Array.FindAll(range, set => set).Length), $"range {bit}+{count}");
                Assert.That(bitmap.AnyInRange(bit, count), Is.EqualTo(Array.IndexOf(range, true) >= 0), $"any in {bit}+{count}");
            }

            byte[] bytes = new byte[bitCount / 8];
            bitmap.Write(bytes);
            PbtBitmap.Read(bytes, out PbtBitmap read);
            PbtBitmap union = bitmap;
            union.Or(otherBitmap);
            PbtBitmap difference = bitmap;
            difference.AndNot(otherBitmap);
            for (int bit = 0; bit < bitCount; bit++)
            {
                Assert.That(read.IsSet(bit), Is.EqualTo(reference[bit]), $"read bit {bit}");
                Assert.That(union.IsSet(bit), Is.EqualTo(reference[bit] || other[bit]), $"union bit {bit}");
                Assert.That(difference.IsSet(bit), Is.EqualTo(reference[bit] && !other[bit]), $"difference bit {bit}");
            }

            bitmap.Clear(Array.IndexOf(reference, true));
            Assert.That(bitmap.PopCount(), Is.EqualTo(Array.FindAll(reference, set => set).Length - 1));
            Assert.That(default(PbtBitmap).IsEmpty && !bitmap.IsEmpty && default(PbtBitmap).NextSetBit(0) == -1);
        }
    }
}
