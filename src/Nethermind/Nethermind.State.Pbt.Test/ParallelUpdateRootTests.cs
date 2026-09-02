// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class ParallelUpdateRootTests
{
    [TestCase(PbtNodeLayout.Record)]
    [TestCase(PbtNodeLayout.HashBucket)]
    public async Task Concurrent_independent_folds_equal_serial_fold(PbtNodeLayout layout)
    {
        (byte[] Key, byte[]? Value)[] entries = Entries(seed: 8297, count: 2000);
        PbtTreeHarness serial = new(layout);
        ValueHash256 expectedRoot = serial.ApplyBatch(entries);
        string[] expectedRecords = serial.CanonicalRecords();

        Task<(ValueHash256 Root, string[] Records)>[] tasks = new Task<(ValueHash256, string[])>[8];
        for (int worker = 0; worker < tasks.Length; worker++)
        {
            tasks[worker] = Task.Run(() =>
            {
                PbtTreeHarness parallel = new(layout);
                return (parallel.ApplyBatch(entries), parallel.CanonicalRecords());
            });
        }

        (ValueHash256 Root, string[] Records)[] results = await Task.WhenAll(tasks);
        foreach ((ValueHash256 root, string[] records) in results)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root, Is.EqualTo(expectedRoot));
                Assert.That(records, Is.EqualTo(expectedRecords));
            }
        }
    }

    [Test]
    public async Task Concurrent_serial_and_layout_folds_match_across_mutation_sequences()
    {
        (byte[] Key, byte[]? Value)[] initial = Entries(seed: 17, count: 1000);
        (byte[] Key, byte[]? Value)[] changes = Changes(initial);
        Task<PbtTreeHarness>[] tasks =
        [
            Task.Run(() => Apply(PbtNodeLayout.Record, initial, changes)),
            Task.Run(() => Apply(PbtNodeLayout.HashBucket, initial, changes)),
        ];

        PbtTreeHarness[] trees = await Task.WhenAll(tasks);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trees[1].RootHash, Is.EqualTo(trees[0].RootHash));
            Assert.That(trees[1].CanonicalRecords(), Is.EqualTo(trees[0].CanonicalRecords()));
        }
    }

    private static PbtTreeHarness Apply(PbtNodeLayout layout, params (byte[] Key, byte[]? Value)[][] batches)
    {
        PbtTreeHarness tree = new(layout);
        foreach ((byte[] Key, byte[]? Value)[] batch in batches) tree.ApplyBatch(batch);
        return tree;
    }

    private static (byte[] Key, byte[]? Value)[] Entries(int seed, int count)
    {
        Random random = new(seed);
        (byte[] Key, byte[]? Value)[] entries = new (byte[], byte[]?)[count];
        for (int index = 0; index < entries.Length; index++)
        {
            byte[] key = new byte[2 + random.Next(63)];
            key[0] = (byte)index;
            random.NextBytes(key.AsSpan(1));
            byte[] value = new byte[32];
            random.NextBytes(value);
            entries[index] = (key, value);
        }
        return entries;
    }

    private static (byte[] Key, byte[]? Value)[] Changes((byte[] Key, byte[]? Value)[] initial)
    {
        List<(byte[] Key, byte[]? Value)> changes = [];
        for (int index = 0; index < initial.Length; index += 3)
        {
            changes.Add((initial[index].Key, index % 2 == 0 ? null : Value((byte)index)));
        }
        return [.. changes];
    }

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }
}
