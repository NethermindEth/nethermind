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
    [Test]
    public void FoldsPerStemAndDrainEmpties()
    {
        using PbtWriteBatchBuilder builder = new();
        Stem first = TestStem(0x80, 1);
        Stem second = TestStem(0x00, 2);

        Assert.That(builder.HasDirtyStems, Is.False);

        builder.SetLeaf(first, 40, Value(40));
        builder.SetLeafRange(first, 10, Run(10, 3));
        builder.SetLeaf(second, 7, Value(7));

        Assert.That(builder.HasDirtyStems, Is.True);

        using (PbtWriteBatchSet batches = builder.DrainToWriteBatches(PbtTiling.FourLevel))
        {
            Assert.That(batches.Count, Is.EqualTo(2));
            AssertEntry(batches[PbtPartition.Storage], first, [10, 11, 12, 40]);
            AssertEntry(batches[PbtPartition.Account], second, [7]);
        }

        Assert.That(builder.HasDirtyStems, Is.False, "the drain hands every map to the batch");
        using PbtWriteBatchSet drained = builder.DrainToWriteBatches(PbtTiling.FourLevel);
        Assert.That(drained.Count, Is.Zero);
    }

    /// <summary>
    /// A whole stem lands in one call, its sub-indices ascending but sparse, whether the stem is new —
    /// where the leaves seed a single map — or already dirtied, where they fold into the map it has.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void SetLeavesFoldsAWholeStem(bool alreadyDirtied)
    {
        using PbtWriteBatchBuilder builder = new();
        Stem stem = TestStem(0x80, 1);
        byte[] subIndices = [0, 3, 200, 255];

        builder.SetLeaves(stem, [], []);
        Assert.That(builder.HasDirtyStems, Is.False, "a group with no leaves dirties nothing");

        if (alreadyDirtied) builder.SetLeaf(stem, 7, Value(7));
        builder.SetLeaves(stem, subIndices, Values(subIndices));

        using PbtWriteBatchSet batches = builder.DrainToWriteBatches(PbtTiling.FourLevel);
        PbtWriteBatch batch = batches[PbtPartition.Storage];
        Assert.That(batch.Count, Is.EqualTo(1));
        AssertEntry(batch, stem, alreadyDirtied ? [0, 3, 7, 200, 255] : [0, 3, 200, 255]);
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

        // Interleave stems so individual stems are updated concurrently.
        Random rng = new(42);
        for (int i = work.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (work[i], work[j]) = (work[j], work[i]);
        }

        Parallel.ForEach(work, item => builder.SetLeaf(TestStem(0x80, item.Stem), (byte)item.SubIndex, Value(item.Stem * subIndices + item.SubIndex)));

        using PbtWriteBatchSet batches = builder.DrainToWriteBatches(PbtTiling.FourLevel);
        PbtWriteBatch batch = batches[PbtPartition.Storage];
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

    [Test]
    public void DrainSeparatesPartitionBatchesWithoutGlobalBuckets()
    {
        byte[] firstBytes = [0x00, 0x00, 0x0F, 0x10, 0x80, 0x80, 0x80, 0xFF];

        using PbtWriteBatchBuilder builder = new();
        for (int i = 0; i < firstBytes.Length; i++) builder.SetLeaf(TestStem(firstBytes[i], i), 0, Value(i));

        using PbtWriteBatchSet batches = builder.DrainToWriteBatches(PbtTiling.FourLevel);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batches[PbtPartition.Account].Count, Is.EqualTo(3));
            Assert.That(batches[PbtPartition.Code].Count, Is.EqualTo(1));
            Assert.That(batches[PbtPartition.Storage].Count, Is.EqualTo(4));
            foreach (PbtPartition partition in PbtPartitions.All)
            {
                PbtWriteBatch batch = batches[partition];
                Assert.That(batch.Buckets.Length, Is.Zero, $"{partition} buckets");
                foreach (PbtWriteBatch.StemEntry entry in batch.Entries)
                {
                    Assert.That(PbtPartitions.Of(entry.Stem), Is.EqualTo(partition), $"{partition} entry");
                }
            }
        }
    }

    /// <summary>
    /// A shard whose entry array grew large is replaced on drain rather than cleared, so the builder has
    /// to come out of that empty and usable just as a cleared one does.
    /// </summary>
    [Test]
    public void LargeShardIsReplacedOnDrainAndStaysUsable()
    {
        const int stems = 2_000; // past the count at which a shard's entry array becomes a large object

        using PbtWriteBatchBuilder builder = new();
        for (int i = 0; i < stems; i++) builder.SetLeaf(TestStem(0x80, i), 0, Value(i));

        using (PbtWriteBatchSet batches = builder.DrainToWriteBatches(PbtTiling.FourLevel))
        {
            Assert.That(batches[PbtPartition.Storage].Count, Is.EqualTo(stems));
        }

        Assert.That(builder.HasDirtyStems, Is.False);

        builder.SetLeaf(TestStem(0x80, 0), 1, Value(1));
        using PbtWriteBatchSet reused = builder.DrainToWriteBatches(PbtTiling.FourLevel);
        Assert.That(reused[PbtPartition.Storage].Count, Is.EqualTo(1));
        AssertEntry(reused[PbtPartition.Storage], TestStem(0x80, 0), [1]);
    }

    /// <summary>A batch a producer fills itself carries no buckets, leaving the descent to partition its entries.</summary>
    [Test]
    public void HandBuiltBatch_CarriesNoBuckets()
    {
        using PbtWriteBatch batch = new(estimatedStems: 1, buckets: null);
        batch.Add(TestStem(0x80, 1), PbtStemChanges.Rent().Set(0, Value(0)));
        Assert.That(batch.Buckets.IsEmpty);
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
            // Keep the value alive while copying its span.
            ValueHash256 value = Value(startSubIndex + i);
            value.Bytes.CopyTo(values.AsSpan(i * ValueHash256.MemorySize));
        }

        return values;
    }

    /// <summary><see cref="Value"/> of each of <paramref name="subIndices"/>, as <see cref="PbtWriteBatchBuilder.SetLeaves"/> takes them.</summary>
    private static ValueHash256[] Values(byte[] subIndices)
    {
        ValueHash256[] values = new ValueHash256[subIndices.Length];
        for (int i = 0; i < subIndices.Length; i++) values[i] = Value(subIndices[i]);
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
