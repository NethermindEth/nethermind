// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class Eip8297CanonicalTreeTests
{
    [Test]
    public void Trie_updater_matches_independent_oracle_through_variable_length_mutations()
    {
        using PbtTreeHarness tree = new();
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

    [Test]
    public void Randomized_variable_length_sequences_match_oracle_and_reopen()
    {
        Random random = new(8297);
        byte[][] keys = new byte[128][];
        for (int index = 0; index < keys.Length; index++)
        {
            keys[index] = new byte[2 + random.Next(7)];
            keys[index][0] = (byte)index;
            random.NextBytes(keys[index].AsSpan(1));
        }

        using PbtTreeHarness tree = new();
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

    [Test]
    public void Maximum_length_keys_sharing_all_but_the_final_bit_match_oracle()
    {
        byte[] leftKey = new byte[PbtFullKey.MaxLength];
        byte[] rightKey = new byte[PbtFullKey.MaxLength];
        rightKey[^1] = 1;
        (byte[] Key, byte[]? Value)[] changes = [(leftKey, Value(1)), (rightKey, Value(2))];
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();

        tree.ApplyBatch(changes);
        ApplyOracle(oracle, changes);

        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
    }

    [Test]
    public void Deep_one_sided_divergence_ladder_rejects_buried_prefix_before_store_access()
    {
        const int divergenceCount = 512;
        (byte[] Key, byte[]? Value)[] changes = new (byte[], byte[]?)[divergenceCount + 2];
        for (int bit = 0; bit < divergenceCount; bit++)
        {
            byte[] key = new byte[(divergenceCount / 8) + 1];
            key[bit >> 3] = (byte)(1 << (7 - (bit & 7)));
            changes[bit] = (key, Value((byte)bit));
        }
        changes[^2] = (new byte[divergenceCount / 8], Value(1));
        changes[^1] = (new byte[(divergenceCount / 8) + 1], Value(2));
        CountingPbtStore store = new();

        Assert.Throws<ArgumentException>(() => TrieUpdater.UpdateRoot(store, default, Batch(changes)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Reads, Is.Zero);
            Assert.That(store.Applies, Is.Zero);
            Assert.That(store.Inner.RootHash, Is.EqualTo(default(ValueHash256)));
        }
    }

    [Test]
    public void Split_inside_compressed_prefix_and_delete_merge_stay_canonical()
    {
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        byte[] first = [0x12, 0x00];
        byte[] second = [0x12, 0x80];
        byte[] crossGroupSplit = [0x10, 0x00];

        tree.ApplyBatch([(first, Value(1)), (second, Value(2))]);
        oracle.Insert(first, Value(1));
        oracle.Insert(second, Value(2));
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), "initial split");

        tree.ApplyBatch([(crossGroupSplit, Value(3)), (second, Value(4))]);
        oracle.Insert(crossGroupSplit, Value(3));
        oracle.Insert(second, Value(4));
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), "split inside prefix");

        tree.ApplyBatch([(first, null)]);
        oracle.Delete(first);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), "promotion and prefix merge");
    }

    [Test]
    public void Failed_prefix_batch_is_atomic_after_traversal()
    {
        CountingPbtStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(([0x12], Value(1))));
        PbtPhysicalPayload[] physical = [.. store.Inner.ExportPhysicalPayloads()];
        int applies = store.Applies;
        store.ResetReads();

        Assert.Throws<ArgumentException>(() => TrieUpdater.UpdateRoot(store, root, Batch(
            ([0x12], null), ([0x34, 0x56], Value(2)), ([0x80], Value(3)), ([0x34], Value(4)))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Applies, Is.EqualTo(applies));
            Assert.That(store.Inner.RootHash, Is.EqualTo(root));
            Assert.That(PhysicalRecords(store.Inner.ExportPhysicalPayloads()), Is.EqualTo(PhysicalRecords(physical)));
            Assert.That(store.Reads, Is.GreaterThan(0), "prefix conflicts are detected during traversal");
            Assert.That(store.IssuedGroupPayloads, Has.All.Matches<PbtNodeGroupPayload>(IsDisposed));
        }
    }

    [Test]
    public void Persisted_prefix_conflict_after_a_sibling_was_staged_is_atomic()
    {
        CountingPbtStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(([0x10], Value(1)), ([0x80], Value(2))));
        PbtPhysicalPayload[] physical = [.. store.Inner.ExportPhysicalPayloads()];
        int applies = store.Applies;
        store.ResetReads();

        Assert.Throws<ArgumentException>(() => TrieUpdater.UpdateRoot(store, root, Batch(
            ([0x20], Value(3)), ([0x80, 0x01], Value(4)))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Applies, Is.EqualTo(applies));
            Assert.That(store.Inner.RootHash, Is.EqualTo(root));
            Assert.That(PhysicalRecords(store.Inner.ExportPhysicalPayloads()), Is.EqualTo(PhysicalRecords(physical)));
            Assert.That(store.IssuedGroupPayloads, Has.All.Matches<PbtNodeGroupPayload>(IsDisposed));
        }
    }

    [Test]
    public void Unsorted_ranges_match_oracle_serial_application_and_reopened_records()
    {
        (byte[] Key, byte[]? Value)[] initial = [([0x10], Value(1)), ([0x20], Value(2))];
        (byte[] Key, byte[]? Value)[] changes =
        [
            ([0x12], Value(3)), ([0x80], Value(4)), ([0x1F], Value(5)), ([0x21], Value(6)),
            ([0x02], Value(7)), ([0x40], Value(8)),
        ];
        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        ApplyAll(bulk, serial, oracle, [.. initial]);
        bulk.ApplyBatch(changes);
        foreach ((byte[] key, byte[]? value) in changes) serial.ApplyBatch([(key, value)]);
        ApplyOracle(oracle, changes);
        string[] canonical = bulk.CanonicalRecords();
        string[] physical = PhysicalRecords(bulk);
        bulk.Reopen();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bulk.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(bulk.RootHash, Is.EqualTo(serial.RootHash));
            Assert.That(bulk.CanonicalRecords(), Is.EqualTo(serial.CanonicalRecords()));
            Assert.That(bulk.CanonicalRecords(), Is.EqualTo(canonical));
            Assert.That(PhysicalRecords(bulk), Is.EqualTo(physical));
        }
    }

    [Test]
    public void Earliest_divergence_is_found_inside_an_unsorted_active_range()
    {
        (byte[] Key, byte[]? Value)[] changes =
        [([0x00], Value(1)), ([0x80], Value(2)), ([0x40], Value(3)), ([0x01], Value(4))];
        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        bulk.ApplyBatch(changes);
        foreach ((byte[] key, byte[]? value) in changes) serial.ApplyBatch([(key, value)]);
        ApplyOracle(oracle, changes);
        string[] canonical = bulk.CanonicalRecords();
        string[] physical = PhysicalRecords(bulk);
        bulk.Reopen();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bulk.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(bulk.RootHash, Is.EqualTo(serial.RootHash));
            Assert.That(bulk.CanonicalRecords(), Is.EqualTo(serial.CanonicalRecords()));
            Assert.That(bulk.CanonicalRecords(), Is.EqualTo(canonical));
            Assert.That(PhysicalRecords(bulk), Is.EqualTo(physical));
        }
    }

    [Test]
    public void Interleaved_directions_sharing_a_compressed_prefix_match_oracle()
    {
        (byte[] Key, byte[]? Value)[] changes =
        [
            ([0x12, 0x00], Value(1)), ([0x13, 0x80], Value(2)),
            ([0x12, 0x80], Value(3)), ([0x13, 0x00], Value(4)),
        ];
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        tree.ApplyBatch(changes);
        ApplyOracle(oracle, changes);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
    }

    [Test]
    public void Insertion_order_and_batch_boundaries_do_not_change_root_or_records()
    {
        (byte[] Key, byte[]? Value)[] entries =
        [
            ([0x80], Value(1)), ([0x40], Value(2)), ([0x20], Value(3)),
            ([0x10], Value(4)), ([0x08], Value(5)), ([0x04], Value(6)),
        ];
        using PbtTreeHarness forward = new();
        using PbtTreeHarness reverse = new();
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
    public void Full_key_and_persisted_path_validate_bounds_and_canonical_padding()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtFullKey([]));
        Assert.DoesNotThrow(() => new PbtFullKey(new byte[8192]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtFullKey(new byte[8193]));
        Assert.Throws<ArgumentException>(() => new PbtBitPrefix([0x01], 1));
        Assert.Throws<InvalidDataException>(() => PbtNodePath.Decode([0, 0, 0, 1, 0x01]));

        using PbtTreeHarness tree = new();
        tree.ApplyBatch([([0x12], Value(1))]);
        Assert.Throws<ArgumentException>(() => tree.ApplyBatch([([0x12, 0x34], Value(2))]));
    }

    [Test]
    public void Single_leaf_root_is_exact_tagged_preimage_hash()
    {
        byte[] key = [0x12, 0x34];
        byte[] value = Value(7);
        using PbtTreeHarness tree = new();
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

    [Test]
    public void Shuffled_writes_cover_all_first_group_boundary_destinations()
    {
        int[] order = [7, 15, 2, 10, 0, 12, 5, 1, 9, 14, 3, 8, 6, 11, 4, 13];
        List<(byte[] Key, byte[]? Value)> changes = [];
        foreach (int destination in order)
            changes.Add(([(byte)(destination << 4), (byte)(0x20 + destination)], Value((byte)(destination + 1))));

        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        ApplyAll(bulk, serial, oracle, changes);
        AssertEquivalentAfterReopen(bulk, serial, oracle, "first group boundary");
    }

    [Test]
    public void Common_first_nibble_covers_all_second_group_boundary_destinations()
    {
        int[] order = [13, 1, 8, 3, 15, 0, 6, 11, 4, 14, 2, 10, 7, 5, 12, 9];
        List<(byte[] Key, byte[]? Value)> initial = [];
        List<(byte[] Key, byte[]? Value)> changes = [];
        for (int destination = 0; destination < 16; destination++)
        {
            byte[] key = [(byte)(0xA0 | destination), 0x11];
            initial.Add((key, Value((byte)(destination + 1))));
            changes.Add((key, Value((byte)(0x40 + destination))));
        }
        initial.Add(([0xB1, 0x11], Value(0xEE)));
        List<(byte[] Key, byte[]? Value)> shuffledChanges = [];
        foreach (int destination in order) shuffledChanges.Add(changes[destination]);

        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        ApplyAll(bulk, serial, oracle, initial);
        ApplyAll(bulk, serial, oracle, shuffledChanges);
        AssertEquivalentAfterReopen(bulk, serial, oracle, "second group boundary");
    }

    [Test]
    public void All_second_group_boundary_destinations_fetch_only_touched_groups_once()
    {
        CountingPbtStore store = new();
        PbtWriteBatch initial = new();
        PbtWriteBatch changes = new();
        for (int destination = 0; destination < 16; destination++)
        {
            PbtFullKey key = new([(byte)(0xA0 | destination), 0x11]);
            initial.Set(key, new ValueHash256(Value((byte)(destination + 1))));
            changes.Set(key, new ValueHash256(Value((byte)(0x40 + destination))));
        }
        PbtFullKey untouchedKey = new([0xB1, 0x11]);
        initial.Set(untouchedKey, new ValueHash256(Value(0xEE)));

        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, initial);
        store.ResetReads();
        TrieUpdaterMetrics metrics = new();
        ValueHash256 changedRoot = TrieUpdater.UpdateRoot(store, root, changes, metrics);
        PbtNodePath untouchedGroup = new([0xB0], 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changedRoot, Is.Not.EqualTo(root));
            Assert.That(store.NodeReads, Is.Zero);
            Assert.That(store.GroupReads.Values, Has.All.EqualTo(1));
            Assert.That(store.GroupReads.ContainsKey(untouchedGroup), Is.False);
            Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(store.GroupReads.Count));
            Assert.That(metrics.GroupParses, Is.EqualTo(store.GroupReads.Count));
            Assert.That(metrics.EmittedNodeWrites, Is.EqualTo(store.LastNodeWrites));
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(8)]
    [TestCase(12)]
    [TestCase(13)]
    public void Span_partition_divergence_before_on_and_after_compressed_group_boundaries_matches_oracle(int divergenceBit)
    {
        byte[] leftKey = [0x00, 0x00];
        byte[] existingRightKey = [0x00, 0x08];
        byte[] insertedKey = [0x00, 0x00];
        insertedKey[divergenceBit >> 3] |= (byte)(1 << (7 - (divergenceBit & 7)));
        int trailingBit = divergenceBit == 12 ? 13 : divergenceBit + 1;
        insertedKey[trailingBit >> 3] |= (byte)(1 << (7 - (trailingBit & 7)));
        (byte[] Key, byte[]? Value)[] initial = [(leftKey, Value(1)), (existingRightKey, Value(2))];
        (byte[] Key, byte[]? Value)[] changes = [(insertedKey, Value(3)), (existingRightKey, Value(4))];

        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        ApplyAll(bulk, serial, oracle, [.. initial]);
        ApplyAll(bulk, serial, oracle, [.. changes]);
        AssertEquivalentAfterReopen(bulk, serial, oracle, $"divergence bit {divergenceBit}");
    }

    [TestCase("insert-only")]
    [TestCase("delete-only")]
    [TestCase("replacements")]
    [TestCase("absent-deletes")]
    [TestCase("duplicate-last-write-wins")]
    [TestCase("duplicate-final-delete")]
    [TestCase("mixed-delete-set")]
    [TestCase("shuffled-mixed-delete-set")]
    [TestCase("mixed-canonicalization-boundaries")]
    public void Bulk_mutation_kinds_match_oracle_serial_outcome_and_reopen(string scenarioName)
    {
        (List<(byte[] Key, byte[]? Value)> Initial, List<(byte[] Key, byte[]? Value)> Changes) = Scenario(scenarioName);
        PbtTreeHarness tree = new();
        PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        ApplyAll(tree, serial, oracle, Initial);

        tree.ApplyBatch(Changes);
        foreach ((byte[] key, byte[]? value) in Changes)
            serial.ApplyBatch([(key, value)]);
        ApplyOracle(oracle, Changes);
        string[] records = tree.CanonicalRecords();
        string[] physical = PhysicalRecords(tree);
        tree.Reopen();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), scenarioName);
            Assert.That(tree.RootHash, Is.EqualTo(serial.RootHash), "bulk and serial roots");
            Assert.That(tree.CanonicalRecords(), Is.EqualTo(serial.CanonicalRecords()), "bulk and serial canonical records");
            Assert.That(PhysicalRecords(tree), Is.EqualTo(PhysicalRecords(serial)), "bulk and serial physical records");
            Assert.That(tree.CanonicalRecords(), Is.EqualTo(records), "canonical records survive reopen");
            Assert.That(PhysicalRecords(tree), Is.EqualTo(physical), "physical groups survive reopen");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Mixed_batch_stages_only_effective_leaf_outcomes(bool deleteKeyExists)
    {
        CountingPbtStore store = new();
        byte[] deleteKeyBytes = [0x00];
        byte[] setKeyBytes = [0x80];
        ValueHash256 root = deleteKeyExists
            ? TrieUpdater.UpdateRoot(store, default, Batch((deleteKeyBytes, Value(1))))
            : default;

        ValueHash256 rootAfterUpdate = TrieUpdater.UpdateRoot(store, root, Batch(
            (deleteKeyBytes, null), (setKeyBytes, Value(2))));

        PbtFullKey deleteKey = new(deleteKeyBytes);
        PbtFullKey setKey = new(setKeyBytes);
        bool hasDeleteMutation = store.LastLeafMutations.TryGetValue(deleteKey, out ValueHash256? deleteValue);
        bool hasSetMutation = store.LastLeafMutations.TryGetValue(setKey, out ValueHash256? setValue);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootAfterUpdate, Is.Not.EqualTo(default(ValueHash256)));
            Assert.That(store.LastLeafMutations, Has.Count.EqualTo(deleteKeyExists ? 2 : 1));
            Assert.That(hasDeleteMutation, Is.EqualTo(deleteKeyExists));
            Assert.That(deleteValue, Is.Null);
            Assert.That(hasSetMutation, Is.True);
            Assert.That(setValue, Is.EqualTo(new ValueHash256(Value(2))));
            Assert.That(store.Applies, Is.EqualTo(deleteKeyExists ? 2 : 1));
        }
    }

    [Test]
    public void Mixed_bulk_updates_sharing_a_long_persisted_prefix_load_each_branch_once()
    {
        CountingPbtStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(
            ([0x12, 0x34, 0x50], Value(1)),
            ([0x12, 0x34, 0x58], Value(2)),
            ([0x12, 0x34, 0x60], Value(3)),
            ([0x12, 0x34, 0x70], Value(4))));
        store.ResetReads();
        TrieUpdaterMetrics metrics = new();

        root = TrieUpdater.UpdateRoot(store, root, Batch(
            ([0x12, 0x34, 0x50], null),
            ([0x12, 0x34, 0x58], Value(5)),
            ([0x12, 0x34, 0x64], Value(6)),
            ([0x12, 0x34, 0x7F], null)), metrics);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.Not.EqualTo(default(ValueHash256)));
            Assert.That(store.NodeReads, Is.Zero, "the updater never falls back to per-node reads");
            Assert.That(store.Reads, Is.EqualTo(store.GroupReads.Count), "each owning group is fetched once");
            Assert.That(store.GroupReads, Is.Not.Empty);
            Assert.That(store.GroupReads.Values, Has.All.EqualTo(1), "mixed changes share each cached group lease");
            Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(store.GroupReads.Count));
            Assert.That(metrics.GroupParses, Is.LessThanOrEqualTo(metrics.PhysicalGroupFetches));
            Assert.That(metrics.GroupCacheProbes, Is.GreaterThanOrEqualTo(metrics.PhysicalGroupFetches));
            Assert.That(metrics.EmittedNodeWrites, Is.EqualTo(store.LastNodeWrites));
            Assert.That(store.IssuedGroupPayloads, Has.All.Matches<PbtNodeGroupPayload>(IsDisposed));
            Assert.That(store.Applies, Is.EqualTo(2));
        }
    }

    [Test]
    public void Same_group_recursion_uses_one_frame_and_suppresses_noop_node_writes()
    {
        CountingPbtStore store = new();
        PbtWriteBatch initial = Batch(([0x00], Value(1)), ([0x40], Value(2)));
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, initial);
        store.ResetReads();
        TrieUpdaterMetrics metrics = new();

        ValueHash256 unchangedRoot = TrieUpdater.UpdateRoot(store, root, initial, metrics);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unchangedRoot, Is.EqualTo(root));
            Assert.That(store.NodeReads, Is.Zero, "the updater never falls back to per-node reads");
            Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(1));
            Assert.That(metrics.GroupParses, Is.EqualTo(1));
            Assert.That(metrics.GroupCacheProbes, Is.EqualTo(1), "same-group logical nodes use the active frame");
            Assert.That(metrics.EmittedNodeWrites, Is.Zero);
            Assert.That(store.LastNodeWrites, Is.Zero);
        }
    }

    [Test]
    public void Boundary_crossing_fetches_only_visited_groups_once()
    {
        CountingPbtStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(
            ([0x00], Value(1)), ([0x08], Value(2)), ([0x80], Value(3)), ([0x88], Value(4))));
        store.ResetReads();
        TrieUpdaterMetrics metrics = new();

        TrieUpdater.UpdateRoot(store, root, Batch(([0x00], Value(5))), metrics);

        PbtNodePath untouchedGroup = new([0x80], 4);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.NodeReads, Is.Zero);
            Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(2), "root group and changed left boundary group");
            Assert.That(metrics.GroupParses, Is.EqualTo(2));
            Assert.That(metrics.GroupCacheProbes, Is.EqualTo(2), "one probe per entered physical group");
            Assert.That(store.GroupReads.Values, Has.All.EqualTo(1));
            Assert.That(store.GroupReads.ContainsKey(untouchedGroup), Is.False, "the untouched right group is not fetched");
            Assert.That(metrics.EmittedNodeWrites, Is.EqualTo(store.LastNodeWrites));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Cached_group_leases_are_released_when_decode_or_apply_fails(bool applyFailure)
    {
        CountingPbtStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(([0x12], Value(1))));
        int appliesBeforeFailure = store.Applies;
        if (applyFailure) store.ThrowOnApply = true;
        else store.OverrideGroup = static _ => PbtNodeGroupPayload.FromLease(RefCountingMemory.Wrapping([0x01]));

        Action update = () => TrieUpdater.UpdateRoot(store, root, Batch(([0x12], Value(2))));
        if (applyFailure) Assert.Throws<InvalidOperationException>(update);
        else Assert.Throws<InvalidDataException>(update);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Applies, Is.EqualTo(appliesBeforeFailure + (applyFailure ? 1 : 0)));
            Assert.That(store.IssuedGroupPayloads, Has.All.Matches<PbtNodeGroupPayload>(IsDisposed));
        }
    }

    [Test]
    public void Cached_group_lease_is_released_when_traversal_finds_a_missing_node()
    {
        CountingPbtStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(([0x12], Value(1)), ([0x92], Value(2))));
        int appliesBeforeFailure = store.Applies;
        store.IssuedGroupPayloads.Clear();
        store.OverrideNode = path => path.BitDepth == 0 ? store.Inner.GetNode(path) : null;

        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, root, Batch(([0x12], Value(3)))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Applies, Is.EqualTo(appliesBeforeFailure));
            Assert.That(store.IssuedGroupPayloads, Has.All.Matches<PbtNodeGroupPayload>(IsDisposed));
        }
    }

    [Test]
    public void Non_byte_aligned_prefix_split_and_sibling_promotion_round_trip()
    {
        PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        byte[] first = [0xAA, 0x00];
        byte[] second = [0xAB, 0x00];
        byte[] split = [0xA8, 0x00];
        tree.ApplyBatch([(first, Value(1)), (second, Value(2))]);
        oracle.Insert(first, Value(1));
        oracle.Insert(second, Value(2));
        tree.ApplyBatch([(split, Value(3)), (second, Value(4))]);
        oracle.Insert(split, Value(3));
        oracle.Insert(second, Value(4));
        tree.ApplyBatch([(first, null)]);
        oracle.Delete(first);
        string[] records = tree.CanonicalRecords();
        tree.Reopen();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(tree.CanonicalRecords(), Is.EqualTo(records));
            Assert.That(tree.PhysicalPayloads, Is.Not.Empty);
        }
    }

    [Test]
    public void Failed_batches_never_apply_or_change_state_for_prefix_missing_or_mismatched_nodes()
    {
        foreach (bool hashMismatch in new[] { false, true })
        {
            CountingPbtStore store = new();
            ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(([0x12], Value(1))));
            PbtPhysicalPayload[] before = [.. store.Inner.ExportPhysicalPayloads()];
            int appliesBeforeFailure = store.Applies;
            store.OverrideNode = hashMismatch
                ? static _ => PbtNodeCodec.Encode(new PbtLeafNode(new PbtFullKey([0xEE]), Value(9)))
                : static _ => null;

            PbtWriteBatch changes = hashMismatch
                ? Batch(([0x12], Value(2)))
                : Batch(([0x12], null), ([0x34], Value(2)), ([0x34, 0x56], Value(3)));
            if (hashMismatch)
                Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, root, changes));
            else
                Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, root, changes));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(store.Applies, Is.EqualTo(appliesBeforeFailure), hashMismatch ? "hash mismatch" : "missing node");
                Assert.That(store.Inner.RootHash, Is.EqualTo(root));
                Assert.That(PhysicalRecords(store.Inner.ExportPhysicalPayloads()), Is.EqualTo(PhysicalRecords(before)));
                Assert.That(store.IssuedGroupPayloads, Has.All.Matches<PbtNodeGroupPayload>(IsDisposed));
            }
        }
    }

    private static (List<(byte[] Key, byte[]? Value)> Initial, List<(byte[] Key, byte[]? Value)> Changes) Scenario(string name) => name switch
    {
        "insert-only" => ([], [([0x10], Value(1)), ([0x20], Value(2))]),
        "delete-only" => ([([0x10], Value(1)), ([0x20], Value(2))], [([0x10], null), ([0xFF], null)]),
        "replacements" => ([([0x10], Value(1)), ([0x20], Value(2))], [([0x10], Value(3)), ([0x20], Value(4))]),
        "absent-deletes" => ([([0x10], Value(1))], [([0xFF], null), ([0xEE], null)]),
        "duplicate-last-write-wins" => ([], [([0x10], Value(1)), ([0x10], Value(2)), ([0x10], null), ([0x10], Value(3))]),
        "duplicate-final-delete" => ([([0x10], Value(1))], [([0x10], Value(2)), ([0x10], null), ([0x10], Value(3)), ([0x10], null)]),
        "mixed-delete-set" => ([([0x10], Value(1)), ([0x20], Value(2))], [([0x10], null), ([0x30], Value(3)), ([0x20], Value(4))]),
        "shuffled-mixed-delete-set" => ([([0x10], Value(1)), ([0x20], Value(2)), ([0x80], Value(3))],
            [([0x80], null), ([0x21], Value(4)), ([0x10], null), ([0x20], Value(5)), ([0x11], Value(6))]),
        "mixed-canonicalization-boundaries" => (
            [([0xA8, 0x00], Value(1)), ([0xAA, 0x00], Value(2)), ([0xAB, 0x00], Value(3)),
             ([0xB0, 0x00], Value(4)), ([0xB8, 0x00], Value(5)), ([0xF0, 0x00], Value(6))],
            [([0xAA, 0x00], Value(20)), ([0xAB, 0x00], null), ([0xA9, 0x00], Value(7)),
             ([0xA8, 0x00], null), ([0xA8, 0x00], Value(8)), ([0xA8, 0x00], null),
             ([0xAC, 0x00], null), ([0xB8, 0x00], null), ([0xB4, 0x00], Value(9)),
             ([0xF0, 0x00], Value(10)), ([0xF0, 0x00], null), ([0xF0, 0x00], Value(11))]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static void ApplyAll(PbtTreeHarness tree, PbtTreeHarness serial, EipReferenceTree oracle, List<(byte[] Key, byte[]? Value)> changes)
    {
        tree.ApplyBatch(changes);
        foreach ((byte[] key, byte[]? value) in changes)
            serial.ApplyBatch([(key, value)]);
        ApplyOracle(oracle, changes);
    }

    private static void AssertEquivalentAfterReopen(PbtTreeHarness bulk, PbtTreeHarness serial, EipReferenceTree oracle, string scenario)
    {
        string[] canonical = bulk.CanonicalRecords();
        string[] physical = PhysicalRecords(bulk);
        bulk.Reopen();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bulk.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), scenario);
            Assert.That(bulk.RootHash, Is.EqualTo(serial.RootHash), "bulk and serial roots");
            Assert.That(bulk.CanonicalRecords(), Is.EqualTo(serial.CanonicalRecords()), "bulk and serial canonical records");
            Assert.That(PhysicalRecords(bulk), Is.EqualTo(PhysicalRecords(serial)), "bulk and serial physical records");
            Assert.That(bulk.CanonicalRecords(), Is.EqualTo(canonical), "canonical records survive reopen");
            Assert.That(PhysicalRecords(bulk), Is.EqualTo(physical), "physical groups survive reopen");
        }
    }

    private static void ApplyOracle(EipReferenceTree oracle, IEnumerable<(byte[] Key, byte[]? Value)> changes)
    {
        foreach ((byte[] key, byte[]? value) in changes)
        {
            if (value is null) oracle.Delete(key);
            else oracle.Insert(key, value);
        }
    }

    private static PbtWriteBatch Batch(params (byte[] Key, byte[]? Value)[] changes)
    {
        PbtWriteBatch batch = new();
        foreach ((byte[] key, byte[]? value) in changes)
        {
            PbtFullKey fullKey = new(key);
            if (value is null) batch.Delete(fullKey);
            else batch.Set(fullKey, new ValueHash256(value));
        }
        return batch;
    }

    private static string[] PhysicalRecords(PbtTreeHarness tree) => PhysicalRecords(tree.PhysicalPayloads);

    private static string[] PhysicalRecords(IEnumerable<PbtPhysicalPayload> payloads)
    {
        List<string> records = [];
        foreach (PbtPhysicalPayload payload in payloads)
            records.Add(Convert.ToHexString(payload.Key.Span) + Convert.ToHexString(payload.Payload.Span));
        records.Sort(StringComparer.Ordinal);
        return [.. records];
    }

    private static string[] PhysicalRecords(PbtPhysicalPayload[] payloads) => PhysicalRecords((IEnumerable<PbtPhysicalPayload>)payloads);

    private sealed class CountingPbtStore : IPbtStore
    {
        internal PbtNodeGroupStore Inner { get; } = new();
        internal int Reads { get; private set; }
        internal int NodeReads { get; private set; }
        internal int Applies { get; private set; }
        internal int LastNodeWrites { get; private set; }
        internal Dictionary<PbtFullKey, ValueHash256?> LastLeafMutations { get; } = [];
        internal Dictionary<PbtNodePath, int> GroupReads { get; } = [];
        internal List<PbtNodeGroupPayload> IssuedGroupPayloads { get; } = [];
        internal Func<PbtNodePath, byte[]?>? OverrideNode { get; set; }
        internal Func<PbtNodePath, PbtNodeGroupPayload?>? OverrideGroup { get; set; }
        internal bool ThrowOnApply { get; set; }

        public byte[]? GetNode(PbtNodePath path)
        {
            Reads++;
            NodeReads++;
            return OverrideNode is { } overrideNode ? overrideNode(path) : Inner.GetNode(path);
        }

        public PbtNodeGroupPayload? GetNodeGroup(PbtNodePath groupKey)
        {
            Reads++;
            GroupReads[groupKey] = GroupReads.GetValueOrDefault(groupKey) + 1;
            if (OverrideGroup is { } overrideGroup)
            {
                PbtNodeGroupPayload? overriddenPayload = overrideGroup(groupKey);
                if (overriddenPayload is not null) IssuedGroupPayloads.Add(overriddenPayload);
                return overriddenPayload;
            }
            if (OverrideNode is { } overrideNode)
            {
                List<PbtNodeRecord> records = [];
                for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
                {
                    if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
                    PbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
                    byte[]? encoding = overrideNode(path);
                    if (encoding is not null) records.Add(new PbtNodeRecord(path, encoding));
                }

                if (records.Count == 0) return null;
                BufferWriter writer = new(PooledRefCountingMemoryProvider.Instance);
                try
                {
                    PbtNodeGroupCodec.Encode(ref writer, groupKey, records);
                    PbtNodeGroupPayload payload = PbtNodeGroupPayload.FromLease(writer.Detach()!);
                    IssuedGroupPayloads.Add(payload);
                    return payload;
                }
                catch
                {
                    writer.Dispose();
                    throw;
                }
            }
            PbtNodeGroupPayload? innerPayload = Inner.GetNodeGroup(groupKey);
            if (innerPayload is not null) IssuedGroupPayloads.Add(innerPayload);
            return innerPayload;
        }

        public void Apply(in ValueHash256 newRoot, IReadOnlyList<PbtLeafMutation> leaves, IReadOnlyList<PbtNodeMutation> nodes)
        {
            Applies++;
            LastLeafMutations.Clear();
            foreach (PbtLeafMutation mutation in leaves) LastLeafMutations[mutation.Key] = mutation.Value;
            LastNodeWrites = nodes.Count;
            if (ThrowOnApply) throw new InvalidOperationException("Configured apply failure.");
            Inner.Apply(newRoot, leaves, nodes);
        }

        internal void ResetReads()
        {
            Reads = 0;
            NodeReads = 0;
            LastNodeWrites = 0;
            GroupReads.Clear();
        }
    }

    private static bool IsDisposed(PbtNodeGroupPayload payload)
    {
        try
        {
            _ = payload.Memory;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
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
