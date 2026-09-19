// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>Folds whole slot runs through <see cref="TrieUpdater"/> against the EIP-8297 reference tree.</summary>
public class SlotRunFoldTests
{
    private const int RunGroupDepth = PbtStoragePath.KeyLength * 8 - PbtFourLevelGroupGeometry.LevelsPerGroup;

    private static byte[] Slot(uint slot) => PbtStateKey.Storage(TestItem.AddressA, slot).Bytes.ToArray();

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }

    [TestCase(16, 3, "full run, rewritten with three leaves")]
    [TestCase(16, 1, "full run, rewritten with one leaf")]
    [TestCase(2, 0, "two leaves, deleted")]
    [TestCase(1, 5, "one promoted leaf, widened")]
    public void Runs_fold_whole_and_the_run_group_exists_only_with_two_leaves(int initialLeaves, int remainingLeaves, string scenario)
    {
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        List<(byte[] Key, byte[]? Value)> writes = [];
        // Slots 64..79 are one storage-zone run; slot 80 is a neighbouring run that must stay untouched.
        writes.Add((Slot(80), Value(0xEE)));
        for (uint slot = 64; slot < 64 + (uint)initialLeaves; slot++) writes.Add((Slot(slot), Value((byte)slot)));
        Apply(writes);

        writes.Clear();
        for (uint slot = 64; slot < 80; slot++) writes.Add((Slot(slot), slot < 64 + (uint)remainingLeaves ? Value((byte)(slot + 100)) : null));
        Apply(writes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), scenario);
            Assert.That(tree.PhysicalPayloads.Any(payload => payload.Key.BitDepth == RunGroupDepth), Is.EqualTo(remainingLeaves >= 2), $"{scenario}: the run's own group holds only branching leaves");
        }

        void Apply(List<(byte[] Key, byte[]? Value)> batch)
        {
            tree.ApplyBatch(batch);
            foreach ((byte[] key, byte[]? value) in batch)
            {
                if (value is null) oracle.Delete(key);
                else oracle.Insert(key, value);
            }
        }
    }

    [Test]
    public void Run_key_helpers_agree_across_key_types([Values(1u, 64u)] uint slot)
    {
        PbtStorageTreeKey key = PbtStateKey.Storage(TestItem.AddressA, slot);
        PbtStorageTreeKey runKey = SlotRun.RunKey(key);
        using (Assert.EnterMultipleScope())
        {
            if (key.Length == PbtPath.KeyLength)
            {
                PbtPath fixedKey = (PbtPath)key;
                Assert.That(SlotRun.RunKey(fixedKey), Is.EqualTo((PbtPath)runKey));
                Assert.That(SlotRun.SlotKey(SlotRun.RunKey(fixedKey), SlotRun.IndexOf(fixedKey)), Is.EqualTo(fixedKey));
            }
            else
            {
                PbtStoragePath fixedKey = (PbtStoragePath)key;
                Assert.That(SlotRun.RunKey(fixedKey), Is.EqualTo((PbtStoragePath)runKey));
                Assert.That(SlotRun.SlotKey(SlotRun.RunKey(fixedKey), SlotRun.IndexOf(fixedKey)), Is.EqualTo(fixedKey));
            }
            PbtStorageTreeKey shortKey = new(key.Bytes[..31]);
            Assert.That(SlotRun.RunKey(shortKey).Bytes[^1], Is.EqualTo(shortKey.Bytes[^1] & 0xF0));
            Assert.That(SlotRun.RunKey(shortKey).Length, Is.EqualTo(31));
        }
    }
}
