// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtTilingTests
{
    [Test]
    public void Physical_layouts_fold_random_mutation_rounds_to_same_canonical_tree()
    {
        Random random = new(42);
        PbtTreeHarness record = new(PbtNodeLayout.Record);
        PbtTreeHarness grouped = new(PbtNodeLayout.HashBucket);
        EipReferenceTree oracle = new();
        byte[][] keys = Keys(random, 256);

        for (int round = 0; round < 12; round++)
        {
            Dictionary<int, byte[]?> batchValues = [];
            for (int index = 0; index < 40; index++)
            {
                int keyIndex = random.Next(keys.Length);
                byte[]? value = random.Next(4) == 0 ? null : RandomValue(random);
                batchValues[keyIndex] = value;
            }
            List<(byte[] Key, byte[]? Value)> writes = [];
            foreach ((int keyIndex, byte[]? value) in batchValues)
            {
                byte[] key = keys[keyIndex];
                writes.Add((key, value));
                if (value is null) oracle.Delete(key);
                else oracle.Insert(key, value);
            }

            ValueHash256 recordRoot = record.ApplyBatch(writes);
            ValueHash256 groupedRoot = grouped.ApplyBatch(writes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recordRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"round {round}");
                Assert.That(groupedRoot, Is.EqualTo(recordRoot), $"round {round}");
                Assert.That(grouped.CanonicalRecords(), Is.EqualTo(record.CanonicalRecords()), $"round {round}");
            }
        }
    }

    [TestCase(PbtNodeLayout.Record, PbtNodeLayout.HashBucket)]
    [TestCase(PbtNodeLayout.HashBucket, PbtNodeLayout.Record)]
    public void Layout_switch_and_reopen_preserve_records_and_support_later_mutations(PbtNodeLayout initial, PbtNodeLayout later)
    {
        Random random = new(7);
        byte[][] keys = Keys(random, 200);
        List<(byte[] Key, byte[]? Value)> initialWrites = [];
        EipReferenceTree oracle = new();
        foreach (byte[] key in keys)
        {
            byte[] value = RandomValue(random);
            initialWrites.Add((key, value));
            oracle.Insert(key, value);
        }
        PbtTreeHarness tree = new(initial);
        tree.ApplyBatch(initialWrites);
        string[] before = tree.CanonicalRecords();

        tree.Layout = later;
        tree.Reopen();
        List<(byte[] Key, byte[]? Value)> changes = [];
        for (int index = 0; index < keys.Length; index += 5)
        {
            byte[]? value = index % 10 == 0 ? null : RandomValue(random);
            changes.Add((keys[index], value));
            if (value is null) oracle.Delete(keys[index]);
            else oracle.Insert(keys[index], value);
        }
        tree.ApplyBatch(changes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.Not.Empty);
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(tree.PhysicalPayloads, Is.Not.Empty);
        }
    }

    private static byte[][] Keys(Random random, int count)
    {
        byte[][] keys = new byte[count][];
        for (int index = 0; index < keys.Length; index++)
        {
            keys[index] = new byte[2 + random.Next(63)];
            keys[index][0] = (byte)index;
            random.NextBytes(keys[index].AsSpan(1));
        }
        return keys;
    }

    private static byte[] RandomValue(Random random)
    {
        byte[] value = new byte[32];
        random.NextBytes(value);
        return value;
    }
}
