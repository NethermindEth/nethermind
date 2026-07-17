// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtWriteBatchBuilderTests
{
    /// <summary>
    /// Leaf and range writes to one stem fold into a single entry carrying all of them, distinct stems
    /// get an entry each, and the drain empties the builder — a second drain yields nothing.
    /// </summary>
    [Test]
    public void FoldsPerStemAndDrainEmpties()
    {
        using PbtWriteBatchBuilder builder = new();
        Stem first = TestStem(0x80, 1);
        Stem second = TestStem(0x00, 2);

        Assert.That(builder.HasDirtyStems, Is.False);

        builder.SetLeaf(first, 40, Value(40));
        builder.SetLeafRange(first, 10, Run(10, 3));  // sub-indices 10, 11, 12
        builder.SetLeaf(second, 7, Value(7));

        Assert.That(builder.HasDirtyStems, Is.True);

        using (PbtWriteBatch batch = builder.DrainToWriteBatch())
        {
            Assert.That(batch.Count, Is.EqualTo(2));
            AssertEntry(batch, first, [10, 11, 12, 40]);
            AssertEntry(batch, second, [7]);
        }

        Assert.That(builder.HasDirtyStems, Is.False, "the drain hands every map to the batch");
        using PbtWriteBatch drained = builder.DrainToWriteBatch();
        Assert.That(drained.Count, Is.Zero);
    }

    /// <summary>
    /// Every write lands when threads race, including on one stem at once (which the shard's lock, held
    /// across the change map's promotion, is what makes safe) and on stems sharing a shard — the stems
    /// here all start 0x80, so they hash to the same shard by construction.
    /// </summary>
    [Test]
    public void ConcurrentWritesToSharedShardAndStemAllLand()
    {
        const int stems = 8;
        const int subIndices = 256;

        using PbtWriteBatchBuilder builder = new();
        List<(int Stem, int SubIndex)> work = [];
        for (int s = 0; s < stems; s++)
        {
            for (int i = 0; i < subIndices; i++) work.Add((s, i));
        }

        // interleave the stems so a stem is in flight on several threads at once
        Random rng = new(42);
        for (int i = work.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (work[i], work[j]) = (work[j], work[i]);
        }

        Parallel.ForEach(work, item => builder.SetLeaf(TestStem(0x80, item.Stem), (byte)item.SubIndex, Value(item.Stem * subIndices + item.SubIndex)));

        using PbtWriteBatch batch = builder.DrainToWriteBatch();
        Assert.That(batch.Count, Is.EqualTo(stems));
        for (int s = 0; s < stems; s++)
        {
            IPbtStemChanges changes = Changes(batch, TestStem(0x80, s));
            Assert.That(changes.Count, Is.EqualTo(subIndices), $"stem {s}");
            for (int i = 0; i < subIndices; i++)
            {
                Assert.That(changes.SubIndexAt(i), Is.EqualTo((byte)i));
                Assert.That(changes.Get(i), Is.EqualTo(Value(s * subIndices + i)));
            }
        }
    }

    private static void AssertEntry(PbtWriteBatch batch, in Stem stem, byte[] expectedSubIndices)
    {
        IPbtStemChanges changes = Changes(batch, stem);
        Assert.That(changes.Count, Is.EqualTo(expectedSubIndices.Length));
        for (int i = 0; i < expectedSubIndices.Length; i++)
        {
            Assert.That(changes.SubIndexAt(i), Is.EqualTo(expectedSubIndices[i]));
            Assert.That(changes.Get(i), Is.EqualTo(Value(expectedSubIndices[i])));
        }
    }

    private static IPbtStemChanges Changes(PbtWriteBatch batch, in Stem stem)
    {
        foreach (PbtWriteBatch.StemEntry entry in batch.Entries)
        {
            if (entry.Stem == stem) return entry.Changes;
        }

        Assert.Fail($"no entry for stem {stem}");
        return null!;
    }

    /// <summary>A stem starting with <paramref name="firstByte"/> — the shard key — and identified by <paramref name="id"/>.</summary>
    private static Stem TestStem(byte firstByte, int id)
    {
        Span<byte> bytes = stackalloc byte[Stem.Length];
        bytes.Clear();
        bytes[0] = firstByte;
        BinaryPrimitives.WriteInt32LittleEndian(bytes[1..], id);
        return new Stem(bytes);
    }

    /// <summary><paramref name="count"/> values back to back, as <see cref="PbtWriteBatchBuilder.SetLeafRange"/> takes them, matching <see cref="Value"/> of each sub-index from <paramref name="startSubIndex"/>.</summary>
    private static byte[] Run(byte startSubIndex, int count)
    {
        byte[] values = new byte[count * ValueHash256.MemorySize];
        for (int i = 0; i < count; i++)
        {
            // local, not inline: a span over a returned-by-value hash dangles once its temp is reused
            ValueHash256 value = Value(startSubIndex + i);
            value.Bytes.CopyTo(values.AsSpan(i * ValueHash256.MemorySize));
        }

        return values;
    }

    private static ValueHash256 Value(int seed)
    {
        Span<byte> bytes = stackalloc byte[ValueHash256.MemorySize];
        bytes.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, seed);
        bytes[31] = 0xAB; // keep it non-zero so it is not mistaken for a leaf clear
        return new ValueHash256(bytes);
    }
}
