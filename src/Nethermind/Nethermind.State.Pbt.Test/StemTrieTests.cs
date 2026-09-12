// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class StemTrieTests
{
    [TestCase(3)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(63)]
    [TestCase(64)]
    [TestCase(163)]
    [TestCase(247)]
    public void Compressed_prefix_split_delete_and_hoist_match_reference(int divergenceBit)
    {
        byte[] first = new byte[32];
        byte[] second = new byte[32];
        second[divergenceBit >> 3] = (byte)(1 << (7 - (divergenceBit & 7)));
        byte[] firstValue = Value(1);
        byte[] secondValue = Value(2);
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();

        tree.ApplyBatch([(first, firstValue)]);
        oracle.Insert(first, firstValue);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));

        tree.ApplyBatch([(second, secondValue)]);
        oracle.Insert(second, secondValue);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));

        tree.ApplyBatch([(first, null)]);
        oracle.Delete(first);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
        Assert.That(tree.Nodes, Has.Count.EqualTo(1));

        tree.ApplyBatch([(second, null)]);
        Assert.That(tree.RootHash, Is.EqualTo(default(ValueHash256)));
        Assert.That(tree.Nodes, Is.Empty);
    }

    [Test]
    public void Split_inside_long_prefix_and_cross_node_group_rebuild_identically()
    {
        (byte[] Key, byte[]? Value)[] entries =
        [
            (Key(0x12, 0x00, 31), Value(1)),
            (Key(0x12, 0x80, 31), Value(2)),
            (Key(0x10, 0x00, 65), Value(3)),
            (Key(0x12, 0x40, 66), Value(4)),
        ];
        using PbtTreeHarness incremental = new();
        foreach ((byte[] key, byte[]? value) in entries) incremental.ApplyBatch([(key, value)]);
        using PbtTreeHarness rebuilt = new();
        rebuilt.ApplyBatch(entries);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(incremental.RootHash, Is.EqualTo(rebuilt.RootHash));
            Assert.That(incremental.CanonicalRecords(), Is.EqualTo(rebuilt.CanonicalRecords()));
            Assert.That(incremental.Nodes, Has.Count.GreaterThan(4));
        }
    }

    private static byte[] Key(byte first, byte second, int length)
    {
        byte[] key = new byte[length];
        key[0] = first;
        key[1] = second;
        return key;
    }

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }
}
