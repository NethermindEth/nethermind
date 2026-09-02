// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class Eip8297CanonicalTreeTests
{
    private static IEnumerable<TestCaseData> Layouts()
    {
        yield return new TestCaseData(PbtNodeLayout.Record).SetName("Record_layout");
        yield return new TestCaseData(PbtNodeLayout.HashBucket).SetName("Hash_bucket_layout");
    }

    [TestCaseSource(nameof(Layouts))]
    public void Trie_updater_matches_independent_oracle_through_variable_length_mutations(PbtNodeLayout layout)
    {
        PbtTreeHarness tree = new(layout);
        EipReferenceTree oracle = new();
        byte[][] keys = [[0x00], [0x40], [0x41, 0x80], [0xFF, 0x10], [0x12, 0x34, 0x56, 0x78]];
        for (int index = 0; index < keys.Length; index++)
        {
            byte[] value = Value((byte)(index + 1));
            tree.ApplyBatch([(keys[index], value)]);
            oracle.Insert(keys[index], value);
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"insert {index}");
        }

        tree.ApplyBatch([(keys[1], new byte[32]), (keys[2], null)]);
        oracle.Insert(keys[1], new byte[32]);
        oracle.Delete(keys[2]);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
    }

    [TestCaseSource(nameof(Layouts))]
    public void Randomized_variable_length_sequences_match_oracle_and_reopen(PbtNodeLayout layout)
    {
        Random random = new(8297);
        byte[][] keys = new byte[128][];
        for (int index = 0; index < keys.Length; index++)
        {
            keys[index] = new byte[2 + random.Next(7)];
            keys[index][0] = (byte)index;
            random.NextBytes(keys[index].AsSpan(1));
        }

        PbtTreeHarness tree = new(layout);
        EipReferenceTree oracle = new();
        for (int operation = 0; operation < 1000; operation++)
        {
            byte[] key = keys[random.Next(keys.Length)];
            if (random.Next(4) == 0)
            {
                tree.ApplyBatch([(key, null)]);
                oracle.Delete(key);
            }
            else
            {
                byte[] value = new byte[32];
                random.NextBytes(value);
                tree.ApplyBatch([(key, value)]);
                oracle.Insert(key, value);
            }

            if (operation % 100 == 99) tree.Reopen();
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"operation {operation}");
        }
    }

    [TestCaseSource(nameof(Layouts))]
    public void Split_inside_compressed_prefix_and_delete_merge_stay_canonical(PbtNodeLayout layout)
    {
        PbtTreeHarness tree = new(layout);
        EipReferenceTree oracle = new();
        byte[] first = [0x12, 0x00];
        byte[] second = [0x12, 0x80];
        byte[] crossTileSplit = [0x10, 0x00];

        tree.ApplyBatch([(first, Value(1)), (second, Value(2))]);
        oracle.Insert(first, Value(1));
        oracle.Insert(second, Value(2));
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), "initial split");

        tree.ApplyBatch([(crossTileSplit, Value(3)), (second, Value(4))]);
        oracle.Insert(crossTileSplit, Value(3));
        oracle.Insert(second, Value(4));
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), "split inside prefix");

        tree.ApplyBatch([(first, null)]);
        oracle.Delete(first);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), "promotion and prefix merge");
    }

    [TestCaseSource(nameof(Layouts))]
    public void Failed_prefix_batch_is_atomic(PbtNodeLayout layout)
    {
        PbtTreeHarness tree = new(layout);
        byte[] original = [0x12];
        tree.ApplyBatch([(original, Value(1))]);
        ValueHash256 root = tree.RootHash;
        string[] records = tree.CanonicalRecords();

        Assert.Throws<ArgumentException>(() => tree.ApplyBatch(
            [(original, null), ([0x34], Value(2)), ([0x34, 0x56], Value(3))]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash, Is.EqualTo(root));
            Assert.That(tree.CanonicalRecords(), Is.EqualTo(records));
        }
    }

    [TestCaseSource(nameof(Layouts))]
    public void Insertion_order_and_batch_boundaries_do_not_change_root_or_records(PbtNodeLayout layout)
    {
        (byte[] Key, byte[]? Value)[] entries =
        [
            ([0x80], Value(1)), ([0x40], Value(2)), ([0x20], Value(3)),
            ([0x10], Value(4)), ([0x08], Value(5)), ([0x04], Value(6)),
        ];
        PbtTreeHarness forward = new(layout);
        PbtTreeHarness reverse = new(layout);
        foreach ((byte[] key, byte[]? value) in entries) forward.ApplyBatch([(key, value)]);
        for (int index = entries.Length - 1; index >= 0; index--) reverse.ApplyBatch([entries[index]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(forward.RootHash, Is.EqualTo(reverse.RootHash));
            Assert.That(forward.CanonicalRecords(), Is.EqualTo(reverse.CanonicalRecords()));
        }
    }

    [Test]
    public void Oracle_matches_independently_assembled_non_byte_aligned_branch_preimages()
    {
        (int PrefixBits, byte[] LeftKey, byte[] RightKey)[] vectors =
        [
            (0, [0x00], [0x80]), (1, [0x00], [0x40]), (7, [0x00], [0x01]),
            (8, [0x00, 0x00], [0x00, 0x80]), (9, [0x00, 0x00], [0x00, 0x40]),
        ];

        foreach ((int prefixBits, byte[] leftKey, byte[] rightKey) in vectors)
        {
            byte[] leftValue = Value(1);
            byte[] rightValue = Value(2);
            byte[] prefix = new byte[(prefixBits + 7) / 8];
            byte[] expected = Hash([1, (byte)(prefixBits >> 8), (byte)prefixBits, .. prefix,
                .. Hash([0, .. leftKey, .. leftValue]), .. Hash([0, .. rightKey, .. rightValue])]);
            EipReferenceTree oracle = new();
            oracle.Insert(leftKey, leftValue);
            oracle.Insert(rightKey, rightValue);
            Assert.That(oracle.Merkelize(), Is.EqualTo(expected), $"prefix length {prefixBits}");
        }
    }

    [Test]
    public void Full_key_and_persisted_locator_validate_bounds_and_canonical_padding()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtFullKey([]));
        Assert.DoesNotThrow(() => new PbtFullKey(new byte[8192]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtFullKey(new byte[8193]));
        Assert.Throws<ArgumentException>(() => new PbtBitPrefix([0x01], 1));
        Assert.Throws<InvalidDataException>(() => PbtNodeLocator.Decode([0, 0, 0, 1, 0x01]));

        PbtTreeHarness tree = new();
        tree.ApplyBatch([([0x12], Value(1))]);
        Assert.Throws<ArgumentException>(() => tree.ApplyBatch([([0x12, 0x34], Value(2))]));
    }

    [Test]
    public void Single_leaf_root_is_exact_tagged_preimage_hash()
    {
        byte[] key = [0x12, 0x34];
        byte[] value = Value(7);
        PbtTreeHarness tree = new();
        tree.ApplyBatch([(key, value)]);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(Hash([0, .. key, .. value])));
    }

    [Test]
    public void Current_key_derivation_emits_exact_zone_lengths()
    {
        byte[] address32 = new byte[32];
        address32[0] = 0xA5;
        PbtFullKey account = Eip8297KeyDerivation.AccountKey(address32, 0);
        PbtFullKey headerStorage = Eip8297KeyDerivation.StorageKey(address32, new Nethermind.Int256.UInt256(63));
        PbtFullKey overflowStorage = Eip8297KeyDerivation.StorageKey(address32, new Nethermind.Int256.UInt256(64));
        PbtFullKey headerCode = Eip8297KeyDerivation.CodeKey(address32, Value(9), 5);
        PbtFullKey code = Eip8297KeyDerivation.CodeKey(address32, Value(9), 300);
        byte[] expectedAddressHash = Hash(address32);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Length, Is.EqualTo(34));
            Assert.That(account.Bytes.Slice(1, 32).ToArray(), Is.EqualTo(expectedAddressHash));
            Assert.That(account.Bytes[0], Is.EqualTo(0));
            Assert.That(headerStorage.Length, Is.EqualTo(34));
            Assert.That(overflowStorage.Length, Is.EqualTo(66));
            Assert.That(overflowStorage.Bytes[0], Is.EqualTo(0xFF));
            Assert.That(headerCode.Length, Is.EqualTo(34));
            Assert.That(headerCode.Bytes[^1], Is.EqualTo(133));
            Assert.That(code.Length, Is.EqualTo(34));
            Assert.That(code.Bytes[0], Is.EqualTo(1));
            Assert.That(code.Bytes[^1], Is.EqualTo(172));
        }
    }

    private static byte[] Hash(byte[] preimage)
    {
        byte[] result = new byte[32];
        global::Blake3.Hasher.Hash(preimage, result);
        return result;
    }

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }
}
