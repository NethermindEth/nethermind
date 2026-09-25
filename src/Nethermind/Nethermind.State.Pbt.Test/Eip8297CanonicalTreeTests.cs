// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;
using static Nethermind.State.Pbt.Test.PbtStoreTestExtensions;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class Eip8297CanonicalTreeTests
{
    [Test]
    public void Account_and_code_zone_folds_match_oracle_and_serial_application_for_34_byte_inputs([Values] bool zeroDeletes)
    {
        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        for (int round = -1; round < 3; round++)
        {
            List<(byte[] Key, byte[]? Value)> changes = [];
            for (int index = 0; index < 64; index++)
            {
                byte[] key = new byte[34];
                key[0] = (byte)(index % 2);
                key[1] = (byte)(index / 4);
                key[^1] = (byte)index;
                byte[]? value = round == -1 || round == 2 || (round == 1 && index % 3 == 0) ? null : Value((byte)(index + round + 1));
                changes.Add((key, Value(1)));
                changes.Add((key, zeroDeletes ? new byte[32] : null));
                if (value is not null) changes.Add((key, value));
            }
            ApplyAll(bulk, serial, oracle, changes);
            AssertEquivalentAfterReopen(bulk, serial, oracle, $"round {round}");
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(131)]
    public void Builder_prepares_selected_nibble_and_independent_single_use_outputs(int shardNibbleIndex)
    {
        using PbtWriteBatchBuilder<PbtStorageTreeKey> builder = new(shardNibbleIndex);
        PbtStorageTreeKey Key(byte nibble, byte suffix)
        {
            byte[] bytes = new byte[Math.Max(2, shardNibbleIndex / 2 + 1)];
            bytes[0] = suffix;
            bytes[shardNibbleIndex / 2] = (byte)((shardNibbleIndex & 1) == 0 ? nibble << 4 : nibble);
            if (shardNibbleIndex < 2) bytes[1] = suffix;
            return new(bytes);
        }
        PbtStorageTreeKey setKey = Key(15, 1);
        PbtStorageTreeKey deleteKey = Key(2, 2);
        PbtStorageTreeKey zeroKey = Key(8, 3);
        builder.Set(setKey, new ValueHash256(Value(1)));
        builder.Delete(setKey);
        builder.Set(setKey, new ValueHash256(Value(2)));
        builder.Set(deleteKey, new ValueHash256(Value(1)));
        builder.SetLeaf(deleteKey, default(ValueHash256));
        builder.Set(zeroKey, default);
        using PbtWriteBatch<PbtStorageTreeKey> first = builder.Build();
        using PbtWriteBatch<PbtStorageTreeKey> second = builder.Build();
        first.Consume(out ArrayPoolList<PbtWriteOperation<PbtStorageTreeKey>> operations, out ArrayPoolList<int> table);
        using ArrayPoolList<PbtWriteOperation<PbtStorageTreeKey>> ownedOperations = operations;
        using ArrayPoolList<int> ownedTable = table;
        first.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.ShardNibbleIndex, Is.EqualTo(shardNibbleIndex));
            Assert.That(builder.Count, Is.EqualTo(3));
            Assert.That(builder.Leaves, Does.Contain(new KeyValuePair<PbtStorageTreeKey, ValueHash256?>(zeroKey, null)));
            Assert.That(table[0], Is.EqualTo((1 << 2) | (1 << 8) | (1 << 15)));
            Assert.That(table.AsSpan().Slice(1, 3).ToArray(), Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(operations, Is.EqualTo(new[]
            {
                PbtWriteOperation<PbtStorageTreeKey>.Delete(deleteKey),
                PbtWriteOperation<PbtStorageTreeKey>.Set(zeroKey, default),
                PbtWriteOperation<PbtStorageTreeKey>.Set(setKey, new ValueHash256(Value(2))),
            }));
            Assert.Throws<InvalidOperationException>(() => first.Consume(out _, out _));
            Assert.Throws<InvalidOperationException>(() => _ = first.Count);
        }
        PbtWriteOperation<PbtStorageTreeKey>[] expected = operations.AsSpan().ToArray();
        operations.AsSpan().Clear();
        table.AsSpan().Clear();
        builder.Reset();
        second.Consume(out ArrayPoolList<PbtWriteOperation<PbtStorageTreeKey>> independentOperations, out ArrayPoolList<int> independentTable);
        using ArrayPoolList<PbtWriteOperation<PbtStorageTreeKey>> ownedIndependentOperations = independentOperations;
        using ArrayPoolList<int> ownedIndependentTable = independentTable;
        using PbtWriteBatch<PbtStorageTreeKey> empty = builder.Build();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(independentOperations, Is.EqualTo(expected));
            Assert.That(independentTable[0], Is.EqualTo((1 << 2) | (1 << 8) | (1 << 15)));
            Assert.That(empty.Count, Is.Zero);
            Assert.That(empty.ShardNibbleIndex, Is.EqualTo(shardNibbleIndex));
        }
    }

    [Test]
    public void Disposed_prepared_batches_release_owned_lists()
    {
        using ArrayPoolList<PbtWriteOperation<PbtStorageTreeKey>> operations = new(1, 1);
        using ArrayPoolList<int> table = new(17, 17);
        using PbtWriteBatch<PbtStorageTreeKey> batch = new(operations, table, 0);

        batch.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ObjectDisposedException>(() => operations.AsSpan());
            Assert.Throws<ObjectDisposedException>(() => table.AsSpan());
            Assert.Throws<InvalidOperationException>(() => batch.Consume(out _, out _));
        }
    }

    [Test]
    public void Updater_releases_consumed_lists_on_success_and_failure([Values] bool fail)
    {
        CountingPbtStore store = new() { ThrowOnApply = fail };
        using ArrayPoolList<PbtWriteOperation<PbtPath>> operations = new(1);
        operations.Add(PbtWriteOperation<PbtPath>.Set(new PbtPath(new byte[PbtPath.KeyLength]), new ValueHash256(Value(1))));
        using ArrayPoolList<int> table = new(17, 17);
        table[0] = 1;
        table[1] = 1;
        using PbtWriteBatch<PbtPath> batch = new(operations, table, 2);
        Action update = () => TrieUpdater.UpdateRoot(store, default, new PbtPartitionBatches { Account = batch }, PbtTreeHarness.FoldQuota(), FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, null);

        // A single zone never fans out, so the failure surfaces unwrapped.
        if (fail) Assert.Throws<InvalidOperationException>(update);
        else update();

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ObjectDisposedException>(() => operations.AsSpan());
            Assert.Throws<ObjectDisposedException>(() => table.AsSpan());
        }
        AssertAllMemoryReleased(store);
    }

    [TestCase(-1)]
    [TestCase(132)]
    public void Builder_rejects_invalid_shard_nibble(int shardNibbleIndex) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtWriteBatchBuilder<PbtStorageTreeKey>(shardNibbleIndex));

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(131)]
    public void Builder_rejects_keys_missing_selected_nibble_before_mutation(int shardNibbleIndex)
    {
        using PbtWriteBatchBuilder<PbtStorageTreeKey> builder = new(shardNibbleIndex);
        PbtStorageTreeKey shortKey = shardNibbleIndex < 2 ? default : new(new byte[shardNibbleIndex / 2]);
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentException>(() => builder.Set(shortKey, default));
            Assert.Throws<ArgumentException>(() => builder.Delete(shortKey));
            Assert.Throws<ArgumentException>(() => builder.SetLeaf(default, null));
            Assert.That(builder.Count, Is.Zero);
        }
    }

    [Test]
    public void Prefix_copy_matches_bit_reference(
        [Range(0, 7)] int sourceOffset,
        [Range(0, 7)] int destinationOffset)
    {
        byte[] source = Bytes.FromHexString("0xa5c37e81f0965ab4d2");
        for (int bitCount = 0; bitCount <= 64; bitCount++)
        {
            byte[] actual = new byte[(destinationOffset + bitCount + 7) >> 3];
            byte[] expected = new byte[actual.Length];
            for (int bit = 0; bit < actual.Length * 8; bit++)
            {
                if (bit < destinationOffset || bit >= destinationOffset + bitCount)
                    actual[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
            }
            actual.CopyTo(expected, 0);
            CopyBitsReference(source, sourceOffset, bitCount, expected, destinationOffset);

            PbtBitPrefix.CopyBits(source.AsSpan(0, (sourceOffset + bitCount + 7) >> 3), sourceOffset, bitCount, actual, destinationOffset);

            Assert.That(actual, Is.EqualTo(expected), $"count {bitCount}");
        }
    }

    [Test]
    public void Path_append_matches_bit_reference(
        [Range(0, 7)] int pathOffset,
        [Values(0, 1, 7, 8, 9, 63, 64, 65, 511)] int prefixLength,
        [Values(0, 1)] int direction)
    {
        byte[] keyBytes = new byte[PbtStorageTreeKey.MaxLength];
        new Random(8297).NextBytes(keyBytes);
        PbtStorageTreeKey key = new(keyBytes);
        int pathDepth = 8 + pathOffset;
        PbtStorageNodePath path = PbtStorageNodePath.FromKey(key, pathDepth);
        byte[] expectedPrefix = new byte[(prefixLength + 7) >> 3];
        CopyBitsReference(keyBytes, pathOffset, prefixLength, expectedPrefix, 0);
        PbtBitPrefix prefix = new(expectedPrefix, prefixLength);
        int resultDepth = pathDepth + prefixLength + 1;
        byte[] expectedPath = new byte[(resultDepth + 7) >> 3];
        CopyBitsReference(keyBytes, 0, pathDepth, expectedPath, 0);
        CopyBitsReference(expectedPrefix, 0, prefixLength, expectedPath, pathDepth);
        expectedPath[(resultDepth - 1) >> 3] |= (byte)(direction << (7 - ((resultDepth - 1) & 7)));

        PbtStorageNodePath appended = path.Append(new CompressedPrefix(EncodePrefix(prefix.Bytes, prefix.BitCount)), direction);
        Assert.That(appended, Is.EqualTo(new PbtStorageNodePath(expectedPath, resultDepth)));
    }

    private static void CopyBitsReference(ReadOnlySpan<byte> source, int sourceOffset, int bitCount, Span<byte> destination, int destinationOffset)
    {
        for (int index = 0; index < bitCount; index++)
        {
            int sourceBit = sourceOffset + index;
            int destinationBit = destinationOffset + index;
            if ((source[sourceBit >> 3] & (1 << (7 - (sourceBit & 7)))) != 0)
                destination[destinationBit >> 3] |= (byte)(1 << (7 - (destinationBit & 7)));
        }
    }

    [Test]
    public void Trie_updater_matches_independent_oracle_through_mutations()
    {
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        byte[][] keys = [AccountKey(0x00), AccountKey(0x40), AccountKey(0x41, 0x80), AccountKey(0xFF, 0x10), AccountKey(0x12, 0x34, 0x56, 0x78)];
        for (int index = 0; index < keys.Length; index++)
        {
            byte[] value = Value((byte)(index + 1));
            tree.ApplyBatch([(keys[index], value)]);
            oracle.Insert(keys[index], value);
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"insert {index}");
        }

        tree.ApplyBatch([(keys[1], new byte[32]), (keys[2], null)]);
        oracle.Delete(keys[1]);
        oracle.Delete(keys[2]);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
    }

    [Test]
    public void Randomized_sequences_match_oracle_and_reopen()
    {
        Random random = new(8297);
        byte[][] keys = new byte[128][];
        for (int index = 0; index < keys.Length; index++)
        {
            keys[index] = ZoneKey(index % 3 == 2 ? "FF" : $"{index % 3:X2}");
            keys[index][1] = (byte)index;
            random.NextBytes(keys[index].AsSpan(2, 1 + random.Next(7)));
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
        byte[] leftKey = ZoneKey("FF");
        byte[] rightKey = ZoneKey("FF");
        rightKey[^1] = 1;
        (byte[] Key, byte[]? Value)[] changes = [(leftKey, Value(1)), (rightKey, Value(2))];
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();

        tree.ApplyBatch(changes);
        ApplyOracle(oracle, changes);

        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
    }

    [TestCase(1)]
    [TestCase(PbtPath.KeyLength - 1)]
    [TestCase(PbtStoragePath.KeyLength - 1)]
    public void Compressed_boundary_cursor_survives_replacement_collapse_and_reinsertion(int differingByte)
    {
        byte[] leftKey = ZoneKey(differingByte < PbtPath.KeyLength ? "00" : "FF");
        byte[] rightKey = (byte[])leftKey.Clone();
        rightKey[differingByte] = 1;
        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();

        ApplyAll(bulk, serial, oracle, [(leftKey, Value(1)), (rightKey, Value(2))]);
        AssertEquivalentAfterReopen(bulk, serial, oracle, "initial boundary");
        ApplyAll(bulk, serial, oracle, [(rightKey, Value(3)), (leftKey, null)]);
        AssertEquivalentAfterReopen(bulk, serial, oracle, "promoted survivor");
        ApplyAll(bulk, serial, oracle, [(leftKey, Value(4)), (rightKey, Value(5))]);
        AssertEquivalentAfterReopen(bulk, serial, oracle, "restored boundary");
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void Rewriting_leaves_with_their_current_values_keeps_root_and_records(int leafCount)
    {
        (byte[] Key, byte[]? Value)[] entries = [(AccountKey(0x80, 0x01), Value(1)), (AccountKey(0x40, 0x02), Value(2)), (AccountKey(0x40, 0x03), Value(3))];
        List<(byte[] Key, byte[]? Value)> changes = [.. entries[..leafCount]];
        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        ApplyAll(bulk, serial, oracle, changes);
        ValueHash256 root = bulk.RootHash;
        string[] canonical = bulk.CanonicalRecords();

        ApplyAll(bulk, serial, oracle, changes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bulk.RootHash, Is.EqualTo(root), "identical rewrite root");
            Assert.That(bulk.CanonicalRecords(), Is.EqualTo(canonical), "identical rewrite records");
        }
        AssertEquivalentAfterReopen(bulk, serial, oracle, "identical rewrite");

        ApplyAll(bulk, serial, oracle, [(entries[0].Key, Value(9))]);
        Assert.That(bulk.RootHash, Is.Not.EqualTo(root), "changed value root");
        AssertEquivalentAfterReopen(bulk, serial, oracle, "changed value");
    }

    [TestCase(new byte[] { 0x90, 0xC0 }, new byte[] { }, TestName = "Right half folds away under a kept left half")]
    [TestCase(new byte[] { 0x90 }, new byte[] { 0xC0 }, TestName = "First right slot folds away and a later one stays")]
    [TestCase(new byte[] { 0x90, 0xC0 }, new byte[] { 0xE0 }, TestName = "Right half folds away except an insert")]
    [TestCase(new byte[] { 0xC0 }, new byte[] { 0x90 }, TestName = "Nested right quarter folds away under a rewritten left")]
    [TestCase(new byte[] { 0x10, 0x20, 0x90 }, new byte[] { 0xC0 }, TestName = "Left half folds away and the right one stays")]
    [TestCase(new byte[] { 0x10, 0x20, 0x90, 0xC0 }, new byte[] { }, TestName = "Both halves fold away")]
    public void Touched_right_half_folding_away_settles_left_sibling_canonically(byte[] deletedPrefixes, byte[] writtenPrefixes)
    {
        using PbtTreeHarness tree = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        List<(byte[] Key, byte[]? Value)> initial = [];
        foreach (byte prefix in new byte[] { 0x10, 0x20, 0x90, 0xC0 }) initial.Add((AccountKey(prefix, 0x00), Value(prefix)));
        ApplyAll(tree, serial, oracle, initial);

        List<(byte[] Key, byte[]? Value)> changes = [];
        foreach (byte prefix in deletedPrefixes) changes.Add((AccountKey(prefix, 0x00), null));
        foreach (byte prefix in writtenPrefixes) changes.Add((AccountKey(prefix, 0x00), Value((byte)(prefix + 1))));
        ApplyAll(tree, serial, oracle, changes);

        AssertEquivalentAfterReopen(tree, serial, oracle, "touched right half");
    }

    [Test]
    public void Split_inside_compressed_prefix_and_delete_merge_stay_canonical()
    {
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        byte[] first = AccountKey(0x12, 0x00);
        byte[] second = AccountKey(0x12, 0x80);
        byte[] crossGroupSplit = AccountKey(0x10, 0x00);

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
    public void Unsorted_ranges_match_oracle_serial_application_and_reopened_records()
    {
        (byte[] Key, byte[]? Value)[] initial = [(AccountKey(0x10), Value(1)), (AccountKey(0x20), Value(2))];
        (byte[] Key, byte[]? Value)[] changes =
        [
            (AccountKey(0x12), Value(3)), (AccountKey(0x80), Value(4)), (AccountKey(0x1F), Value(5)), (AccountKey(0x21), Value(6)),
            (AccountKey(0x02), Value(7)), (AccountKey(0x40), Value(8)),
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
        [(AccountKey(0x00), Value(1)), (AccountKey(0x80), Value(2)), (AccountKey(0x40), Value(3)), (AccountKey(0x01), Value(4))];
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
            (AccountKey(0x12, 0x00), Value(1)), (AccountKey(0x13, 0x80), Value(2)),
            (AccountKey(0x12, 0x80), Value(3)), (AccountKey(0x13, 0x00), Value(4)),
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
            (AccountKey(0x80), Value(1)), (AccountKey(0x40), Value(2)), (AccountKey(0x20), Value(3)),
            (AccountKey(0x10), Value(4)), (AccountKey(0x08), Value(5)), (AccountKey(0x04), Value(6)),
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

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(271)]
    [TestCase(272)]
    [TestCase(527)]
    [TestCase(528)]
    public void Inline_paths_preserve_occupied_bytes_and_value_behavior(int bitDepth)
    {
        byte[] source = new byte[PbtStorageTreeKey.MaxLength];
        source.AsSpan().Fill(0xA5);
        PbtStorageTreeKey key = new(source);
        PbtStorageNodePath fromKey = PbtStorageNodePath.FromKey(key, bitDepth);
        byte[] expected = source.AsSpan(0, (bitDepth + 7) >> 3).ToArray();
        if ((bitDepth & 7) != 0) expected[^1] &= (byte)(0xFF << (8 - (bitDepth & 7)));
        byte[] constructorInput = (byte[])expected.Clone();
        PbtStorageNodePath constructed = new(constructorInput, bitDepth);
        constructorInput.AsSpan().Clear();
        source.AsSpan().Clear();
        PbtNodeGroupLocation<PbtStorageNodePath> location = PbtFourLevelGroupGeometry.Locate(constructed);
        byte[] copiedPath = constructed.ToPathArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(copiedPath, Is.EqualTo(expected));
            Assert.That(fromKey, Is.EqualTo(constructed));
            Assert.That(PbtFourLevelGroupGeometry.PathOf(location.GroupKey, location.Position), Is.EqualTo(constructed));
            if (bitDepth > 0)
            {
                PbtStorageNodePath parent = PbtStorageNodePath.FromKey(key, bitDepth - 1);
                Assert.That(parent.Append(default, key.GetBit(bitDepth - 1)), Is.EqualTo(constructed));
                Assert.That(parent.CompareTo(constructed), Is.LessThan(0));
            }
        }
    }

    [TestCase(1)]
    [TestCase(34)]
    [TestCase(66)]
    public void Inline_full_keys_preserve_copies_and_dictionary_identity(int length)
    {
        byte[] source = new byte[length];
        source.AsSpan().Fill(0xA5);
        PbtStorageTreeKey key = new(source);
        PbtStorageTreeKey copy = key;
        source[^1] ^= 1;
        PbtStorageTreeKey different = new(source);
        source[^1] ^= 1;
        PbtStorageTreeKey equal = new(source);
        Dictionary<PbtStorageTreeKey, int> keys = new() { [key] = 42 };
        source.AsSpan().Clear();
        key = default;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(copy.Length, Is.EqualTo(length));
            Assert.That(copy, Is.EqualTo(equal));
            Assert.That(copy.GetHashCode(), Is.EqualTo(equal.GetHashCode()));
            Assert.That(keys[equal], Is.EqualTo(42));
            Assert.That(copy.CompareTo(equal), Is.Zero);
            Assert.That(copy.CompareTo(different), Is.GreaterThan(0));
            Assert.That(copy.FirstDifferingBit(different), Is.EqualTo(length * 8 - 1));
            Assert.That(copy.FirstDifferingBit(equal), Is.EqualTo(length * 8));
            Assert.That(key.Bytes.Length, Is.Zero);
            Assert.That(key.Equals(default), Is.True);
            Assert.That(key.CompareTo(copy), Is.LessThan(0));
            Assert.That(key.GetHashCode(), Is.EqualTo(default(PbtStorageTreeKey).GetHashCode()));
        }
    }

    [Test]
    public void First_differing_bit_checks_every_bit_from_each_start(
        [Range(0, PbtStorageTreeKey.MaxLength * 8)] int startBit,
        [Values] bool earlierDifferences)
    {
        byte[] bytes = new byte[PbtStorageTreeKey.MaxLength];
        bytes.AsSpan().Fill(0xA5);
        byte[] other = (byte[])bytes.Clone();
        int commonBits = bytes.Length * 8;
        if (earlierDifferences)
        {
            for (int bit = 0; bit < startBit; bit++)
                other[bit >> 3] ^= (byte)(0x80 >> (bit & 7));
        }

        Assert.That(PbtKeyOperations.FirstDifferingBit(bytes, other, startBit), Is.EqualTo(commonBits));
        for (int differingBit = 0; differingBit < commonBits; differingBit++)
        {
            other[differingBit >> 3] ^= (byte)(0x80 >> (differingBit & 7));
            int expected = differingBit < startBit ? commonBits : differingBit;
            Assert.That(PbtKeyOperations.FirstDifferingBit(bytes, other, startBit), Is.EqualTo(expected), $"differing bit {differingBit}");
            other[differingBit >> 3] ^= (byte)(0x80 >> (differingBit & 7));
        }
    }

    [Test]
    public void First_differing_bit_respects_common_length_and_validates_start(
        [Values(0, 1, 31, 32, 33, 34, 63, 64, 65, 66)] int length,
        [Values(0, 1, 31, 32, 33, 34, 63, 64, 65, 66)] int otherLength)
    {
        byte[] bytes = new byte[length];
        byte[] other = new byte[otherLength];
        int commonLength = Math.Min(length, otherLength);
        int commonBits = commonLength * 8;
        bytes.AsSpan(commonLength).Fill(0xFF);
        other.AsSpan(commonLength).Fill(0xFF);

        for (int startBit = 0; startBit <= commonBits; startBit++)
            Assert.That(PbtKeyOperations.FirstDifferingBit(bytes, other, startBit), Is.EqualTo(commonBits), $"start bit {startBit}");

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PbtKeyOperations.FirstDifferingBit(bytes, other, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => PbtKeyOperations.FirstDifferingBit(bytes, other, commonBits + 1));
        }
    }

    [Test]
    public void Matching_prefix_bits_matches_reference_at_offsets_and_word_boundaries(
        [Range(0, 15)] int keyOffset,
        [Values(0, 1, 7, 8, 63, 64, 65, 127, 128, 129, PbtStorageTreeKey.MaxLength * 8)] int requestedBitCount,
        [Values(16, PbtStorageTreeKey.MaxLength)] int keyLength)
    {
        byte[] source = new byte[PbtStorageTreeKey.MaxLength];
        new Random(8297).NextBytes(source);
        PbtStorageTreeKey key = new(source.AsSpan(0, keyLength));
        int bitCount = Math.Min(requestedBitCount, source.Length * 8 - keyOffset);
        byte[] encoding = new byte[sizeof(ushort) + ((bitCount + 7) >> 3)];
        BinaryPrimitives.WriteUInt16BigEndian(encoding, (ushort)bitCount);
        for (int bit = 0; bit < bitCount; bit++)
        {
            int sourceBit = keyOffset + bit;
            int value = (source[sourceBit >> 3] >> (7 - (sourceBit & 7))) & 1;
            encoding[sizeof(ushort) + (bit >> 3)] |= (byte)(value << (7 - (bit & 7)));
        }

        int expectedCount = Math.Min(bitCount, key.BitLength - keyOffset);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.MatchingPrefixBits(default, key, keyOffset), Is.Zero);
            Assert.That(TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.MatchingPrefixBits(new CompressedPrefix(encoding), key, keyOffset), Is.EqualTo(expectedCount));
        }
        for (int differingBit = 0; differingBit < bitCount; differingBit++)
        {
            encoding[sizeof(ushort) + (differingBit >> 3)] ^= (byte)(0x80 >> (differingBit & 7));
            int actual = TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.MatchingPrefixBits(new CompressedPrefix(encoding), key, keyOffset);
            Assert.That(actual, Is.EqualTo(Math.Min(differingBit, expectedCount)), $"differing bit {differingBit}");
            encoding[sizeof(ushort) + (differingBit >> 3)] ^= (byte)(0x80 >> (differingBit & 7));
        }
    }

    [TestCase(67)]
    [TestCase(8192)]
    public void Oversized_keys_and_persisted_leaves_are_rejected(int length)
    {
        byte[] encoding = new byte[3 + length + 32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(encoding.AsSpan(1), (ushort)length);
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageTreeKey(new byte[length]));
            Assert.Throws<InvalidDataException>(() => new PbtNodeReader(encoding));
        }
    }

    [Test]
    public void Invalid_inline_paths_and_default_complete_keys_reject_before_mutation()
    {
        PbtStorageNodePath maximum = new(new byte[66], 528);
        using PbtWriteBatchBuilder<PbtStorageTreeKey> batch = new(0);
        using PbtWriteBatchBuilder<PbtStorageTreeKey> builder = new(2);
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageNodePath(new byte[67], 529));
            Assert.Throws<ArgumentOutOfRangeException>(() => maximum.Append(default, 0));
            Assert.Throws<ArgumentException>(() => new PbtStorageNodePath(Bytes.FromHexString("01"), 1));
            Assert.Throws<ArgumentException>(() => new PbtStorageNodePath([], 8));
            Assert.Throws<ArgumentException>(() => batch.Set(default, default));
            Assert.Throws<ArgumentException>(() => batch.Delete(default));
            Assert.Throws<ArgumentException>(() => builder.SetLeaf(default, default));
            Assert.Throws<ArgumentException>(() => PbtNodeCodec.EncodeLeaf(default(PbtStorageTreeKey)));
            Assert.That(batch.Count, Is.Zero);
            Assert.That(builder.Leaves, Is.Empty);
        }
    }

    [Test]
    public void Full_key_and_persisted_path_validate_bounds_and_canonical_padding()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageTreeKey([]));
        Assert.DoesNotThrow(() => new PbtStorageTreeKey(new byte[PbtStorageTreeKey.MaxLength]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageTreeKey(new byte[PbtStorageTreeKey.MaxLength + 1]));
        Assert.Throws<ArgumentException>(() => new PbtBitPrefix([0x01], 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStoragePath(new byte[PbtStoragePath.KeyLength - 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = (PbtStoragePath)Eip8297KeyDerivation.StorageKey(new byte[32], 1));
        PbtStorageTreeKey slotKey = Eip8297KeyDerivation.StorageKey(new byte[32], PbtKeyDerivation.HeaderStorageOffset);
        Assert.That(slotKey.Length, Is.EqualTo(PbtStoragePath.KeyLength));
        Assert.That((PbtStorageTreeKey)(PbtStoragePath)slotKey, Is.EqualTo(slotKey));
    }

    [Test]
    public void Inline_leaves_survive_root_leaf_update_split_and_collapse()
    {
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        byte[] first = AccountKey(0x12, 0x34);
        byte[] second = AccountKey(0x12, 0x3C);
        byte[] rootLeaf = PbtNodeCodec.EncodeLeaf(new PbtStorageTreeKey(first));

        tree.ApplyBatch([(first, Value(1))]);
        oracle.Insert(first, Value(1));
        tree.Reopen();
        tree.ApplyBatch([(first, Value(2))]);
        oracle.Insert(first, Value(2));
        tree.Reopen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), "updated root leaf");
            Assert.That(tree.Nodes, Has.Count.EqualTo(1));
            Assert.That(tree.Nodes[0].Encoding.ToArray(), Is.EqualTo(rootLeaf), "the root leaf stores its key only");
        }

        tree.ApplyBatch([(second, Value(3))]);
        oracle.Insert(second, Value(3));
        tree.Reopen();
        PbtNodeReader branch = new(tree.Nodes[0].Encoding.Span);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), "split root leaf");
            Assert.That(tree.Nodes, Has.Count.EqualTo(1));
            Assert.That(branch.LeftKey.ToArray(), Is.EqualTo(first));
            Assert.That(branch.RightKey.ToArray(), Is.EqualTo(second));
            Assert.That(branch.LeftHash, Is.EqualTo(PbtNodeCodec.HashLeaf(first, Value(2))), "the split reuses the stored leaf hash");
            Assert.That(branch.RightHash, Is.EqualTo(PbtNodeCodec.HashLeaf(second, Value(3))));
        }

        tree.ApplyBatch([(first, null)]);
        oracle.Delete(first);
        tree.Reopen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), "collapsed to the remaining leaf");
            Assert.That(tree.Nodes, Has.Count.EqualTo(1));
            Assert.That(tree.Nodes[0].Encoding.ToArray(), Is.EqualTo(PbtNodeCodec.EncodeLeaf(new PbtStorageTreeKey(second))));
        }
    }

    [Test]
    public void Single_leaf_root_is_exact_tagged_preimage_hash()
    {
        byte[] key = AccountKey(0x12, 0x34);
        byte[] value = Value(7);
        using PbtTreeHarness tree = new();
        tree.ApplyBatch([(key, value)]);
        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(Hash([0, .. key, .. value])));
    }

    [TestCase(1, 188)]
    [TestCase(34, 189)]
    [TestCase(66, 190)]
    public void Node_hashes_match_independent_preimages_at_stack_threshold(int keyLength, int prefixLength)
    {
        byte[] key = new byte[keyLength];
        key[^1] = 1;
        byte[] value = Value(7);

        int prefixBitCount = prefixLength * 8;
        byte[] prefixBytes = new byte[prefixLength];
        ValueHash256 left = new(Hash([0, .. key, .. value]));
        ValueHash256 right = new(Value(8));
        PbtNodeReader branch = new(PbtNodeCodec.EncodeBranch(prefixBytes, prefixBitCount, left, right, key, []));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PbtNodeCodec.HashLeaf(key, value).Bytes.ToArray(), Is.EqualTo(Hash([0, .. key, .. value])), "leaf");
            // The inline leaf key is not part of the branch preimage.
            Assert.That(PbtNodeCodec.Hash(branch).Bytes.ToArray(), Is.EqualTo(Hash(
                [1, (byte)(prefixBitCount >> 8), (byte)prefixBitCount, .. prefixBytes, .. left.Bytes, .. right.Bytes])), "branch");
        }
    }

    [TestCase(true, 1, 0)]
    [TestCase(true, 66, 0)]
    [TestCase(false, 0, 0)]
    [TestCase(false, 5, 1)]
    [TestCase(false, 8, 2)]
    [TestCase(false, 257, 3)]
    [TestCase(false, ushort.MaxValue, 3)]
    public void Node_reader_borrows_fields_and_preserves_encoding_and_hash(bool leaf, int length, int leafChildren)
    {
        byte[] field = new byte[leaf ? length : (length + 7) / 8];
        field.AsSpan().Fill(0xA0);
        if (!leaf && length % 8 != 0) field[^1] &= (byte)(0xFF << (8 - length % 8));
        ValueHash256 left = new(Value(8));
        ValueHash256 right = new(Value(9));
        byte[] leftKey = (leafChildren & 1) != 0 ? Bytes.FromHexString("A0A1") : [];
        byte[] rightKey = (leafChildren & 2) != 0 ? Bytes.FromHexString(new string('B', 132)) : [];
        byte[] preimage = [1, (byte)(length >> 8), (byte)length, .. field, .. left.Bytes, .. right.Bytes];
        byte[] expected = leaf
            ? [0, (byte)length, .. field]
            : [.. preimage, (byte)leftKey.Length, (byte)rightKey.Length, .. leftKey, .. rightKey];
        byte[] encoding = leaf
            ? PbtNodeCodec.EncodeLeaf(new PbtStorageTreeKey(field))
            : PbtNodeCodec.EncodeBranch(field, length, left, right, leftKey, rightKey);
        byte[] backing = new byte[encoding.Length + 11];
        encoding.CopyTo(backing, 7);
        PbtNodeReader reader = new(backing.AsSpan(7, encoding.Length));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.IsLeaf, Is.EqualTo(leaf));
            Assert.That(reader.Encoding.ToArray(), Is.EqualTo(expected));
            Assert.That(reader.Encoding.Overlaps(backing.AsSpan(), out int offset), Is.True);
            Assert.That(offset, Is.EqualTo(-7));
            if (leaf)
            {
                Assert.That(reader.Key.ToArray(), Is.EqualTo(field));
                Assert.That(reader.Key.Overlaps(backing.AsSpan()), Is.True);
                Assert.Throws<InvalidOperationException>(() => PbtNodeCodec.Hash(new PbtNodeReader(encoding)));
            }
            else
            {
                Assert.That(PbtNodeCodec.Hash(reader).Bytes.ToArray(), Is.EqualTo(Hash(preimage)), "inline leaf keys are not hashed");
                Assert.That(reader.Preimage.ToArray(), Is.EqualTo(preimage));
                Assert.That(reader.Prefix.BitCount, Is.EqualTo(length));
                Assert.That(reader.Prefix.Bytes.ToArray(), Is.EqualTo(field));
                if (length != 0) Assert.That(reader.Prefix.Bytes.Overlaps(backing.AsSpan()), Is.True);
                Assert.That(reader.LeftHash, Is.EqualTo(left));
                Assert.That(reader.RightHash, Is.EqualTo(right));
                Assert.That(reader.LeftKey.ToArray(), Is.EqualTo(leftKey));
                Assert.That(reader.RightKey.ToArray(), Is.EqualTo(rightKey));
                if (leafChildren != 0) Assert.That((leftKey.Length != 0 ? reader.LeftKey : reader.RightKey).Overlaps(backing.AsSpan()), Is.True);
            }
        }
    }

    [Test]
    public void Compressed_prefix_borrows_header_and_bytes(
        [Values(0, 1, 7, 8, 9, 271, 272, 527, 528, ushort.MaxValue)] int bitCount)
    {
        byte[] bytes = new byte[(bitCount + 7) / 8];
        bytes.AsSpan().Fill(0xA0);
        if ((bitCount & 7) != 0) bytes[^1] &= (byte)(0xFF << (8 - (bitCount & 7)));
        byte[] encoding = EncodePrefix(bytes, bitCount);
        CompressedPrefix prefix = new(encoding);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(prefix.BitCount, Is.EqualTo(bitCount));
            Assert.That(prefix.Bytes.ToArray(), Is.EqualTo(bytes));
            if (bitCount != 0)
            {
                Assert.That(prefix.Bytes.Overlaps(encoding.AsSpan(), out int offset), Is.True);
                Assert.That(offset, Is.EqualTo(-2));
            }
            else
            {
                CompressedPrefix empty = default;
                Assert.That(empty.BitCount, Is.EqualTo(prefix.BitCount));
                Assert.That(empty.Bytes.IsEmpty, Is.True);
            }
        }
    }

    [TestCase("")]
    [TestCase("00")]
    [TestCase("000080")]
    [TestCase("0001")]
    [TestCase("000181")]
    [TestCase("000980")]
    [TestCase("0009808000")]
    public void Compressed_prefix_rejects_invalid_encoding(string hex) =>
        Assert.Throws<InvalidDataException>(() => new CompressedPrefix(Bytes.FromHexString(hex)));

    [Test]
    public void Compressed_prefix_append_preserves_bits_and_capacity(
        [Values] bool storage, [Values(0, 1, 7, 8, 9)] int bitCount, [Values(0, 3, 8)] int pathDepth, [Values(0, 1)] int direction)
    {
        if (storage) AssertCompressedAppend<PbtStorageNodePath>(bitCount, pathDepth, direction);
        else AssertCompressedAppend<PbtNodePath>(bitCount, pathDepth, direction);
    }

    private static void AssertCompressedAppend<TPath>(int bitCount, int pathDepth, int direction)
        where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] bytes = new byte[(bitCount + 7) / 8];
        if (bitCount != 0) bytes[0] = 0x80;
        byte[] encoding = EncodePrefix(bytes, bitCount);
        CompressedPrefix prefix = new(encoding);
        TPath path = TPath.Create(new byte[(pathDepth + 7) / 8], pathDepth);
        int depth = pathDepth + bitCount + 1;
        byte[] expected = new byte[(depth + 7) / 8];
        CopyBitsReference(bytes, 0, bitCount, expected, pathDepth);
        expected[(depth - 1) >> 3] |= (byte)(direction << (7 - ((depth - 1) & 7)));
        Assert.That(path.Append(prefix, direction), Is.EqualTo(TPath.Create(expected, depth)));
        if (bitCount == 0) Assert.That(path.Append(default, direction), Is.EqualTo(path.Append(prefix, direction)));
        TPath maximum = TPath.Create(new byte[TPath.MaxBitDepth / 8], TPath.MaxBitDepth);
        Assert.Throws<ArgumentOutOfRangeException>(() => maximum.Append(new CompressedPrefix(encoding), direction));
        Assert.Throws<ArgumentOutOfRangeException>(() => path.Append(new CompressedPrefix(encoding), 2));
        int boundaryDepth = TPath.MaxBitDepth - bitCount - 1;
        TPath boundary = TPath.Create(new byte[(boundaryDepth + 7) / 8], boundaryDepth);
        Assert.That(boundary.Append(prefix, direction).BitDepth, Is.EqualTo(TPath.MaxBitDepth));

        for (int index = 0; index < 1000; index++) _ = path.Append(new CompressedPrefix(encoding), direction);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int index = 0; index < 1000; index++) checksum += path.Append(new CompressedPrefix(encoding), direction).BitDepth;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(checksum, Is.EqualTo(depth * 1000));
            Assert.That(allocated, Is.Zero);
        }
    }

    private static byte[] EncodePrefix(ReadOnlySpan<byte> bytes, int bitCount)
    {
        byte[] encoding = new byte[sizeof(ushort) + bytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(encoding, (ushort)bitCount);
        bytes.CopyTo(encoding.AsSpan(sizeof(ushort)));
        return encoding;
    }

    [TestCaseSource(nameof(MalformedNodeEncodings))]
    public void Node_reader_rejects_malformed_encodings(byte[] encoding) =>
        Assert.Throws<InvalidDataException>(() => new PbtNodeReader(encoding));

    private static IEnumerable<TestCaseData> MalformedNodeEncodings()
    {
        yield return new TestCaseData(Array.Empty<byte>()).SetName("Node_reader_rejects_empty");
        yield return new TestCaseData(Bytes.FromHexString("02")).SetName("Node_reader_rejects_unknown_tag");
        yield return new TestCaseData(Bytes.FromHexString("0000")).SetName("Node_reader_rejects_truncated_header");
        foreach (int length in new[] { 0, 67 })
        {
            byte[] invalidLeaf = new byte[2 + length];
            invalidLeaf[1] = (byte)length;
            yield return new TestCaseData(invalidLeaf).SetName($"Node_reader_rejects_invalid_leaf_length_{length}");
        }
        byte[] leaf = PbtNodeCodec.EncodeLeaf(new PbtStorageTreeKey(Bytes.FromHexString("A0A1")));
        yield return new TestCaseData(leaf[..^1]).SetName("Node_reader_rejects_truncated_leaf");
        yield return new TestCaseData((byte[])[.. leaf, 0]).SetName("Node_reader_rejects_trailing_leaf_bytes");
        byte[] branch = PbtNodeCodec.EncodeBranch(Bytes.FromHexString("A0"), 5, new ValueHash256(Value(1)), new ValueHash256(Value(2)), Bytes.FromHexString("A0"), []);
        yield return new TestCaseData(branch[..^1]).SetName("Node_reader_rejects_truncated_branch");
        yield return new TestCaseData((byte[])[.. branch, 0]).SetName("Node_reader_rejects_trailing_branch_bytes");
        yield return new TestCaseData(branch[..^3]).SetName("Node_reader_rejects_missing_trailer");
        foreach (int keyLength in new[] { 2, 67 })
        {
            byte[] badKeyLength = (byte[])branch.Clone();
            badKeyLength[^2] = (byte)keyLength;
            yield return new TestCaseData(badKeyLength).SetName($"Node_reader_rejects_inline_key_length_{keyLength}");
        }
        byte[] badPadding = (byte[])branch.Clone();
        badPadding[3] |= 1;
        yield return new TestCaseData(badPadding).SetName("Node_reader_rejects_prefix_padding");
        foreach (int offset in new[] { 4, 36 })
        {
            byte[] emptyChild = (byte[])branch.Clone();
            emptyChild.AsSpan(offset, 32).Clear();
            yield return new TestCaseData(emptyChild).SetName($"Node_reader_rejects_empty_child_{offset}");
        }
    }

#if DEBUG
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void Node_reader_rejects_default_and_wrong_kind_access(int kind)
    {
        byte[] encoding = kind == 1
            ? PbtNodeCodec.EncodeLeaf(new PbtStorageTreeKey(Bytes.FromHexString("A0")))
            : PbtNodeCodec.EncodeBranch([], 0, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
        Assert.Throws<InvalidOperationException>(() =>
        {
            PbtNodeReader reader = kind is 0 or 3 ? default : new(encoding);
            if (kind == 0) _ = reader.Encoding.Length;
            else if (kind is 1 or 3) _ = reader.Prefix.BitCount;
            else _ = reader.Key.Length;
        });
    }
#endif

    [TestCase(true)]
    [TestCase(false)]
    public void Node_reader_construction_and_field_access_do_not_allocate(bool leaf)
    {
        byte[] encoding = leaf
            ? PbtNodeCodec.EncodeLeaf(new PbtStorageTreeKey(Bytes.FromHexString("A0")))
            : PbtNodeCodec.EncodeBranch(Bytes.FromHexString("A0"), 5, new ValueHash256(Value(1)), new ValueHash256(Value(2)), Bytes.FromHexString("A1"), Bytes.FromHexString("A2"));
        int expected = ReadNodeFields(encoding);
        for (int index = 0; index < 1000; index++) _ = ReadNodeFields(encoding);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int index = 0; index < 1000; index++) checksum += ReadNodeFields(encoding);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(checksum, Is.EqualTo(expected * 1000));
            Assert.That(allocated, Is.Zero);
        }
    }

    private static int ReadNodeFields(byte[] encoding)
    {
        PbtNodeReader reader = new(encoding);
        return reader.Encoding.Length + (reader.IsLeaf
            ? reader.Key[0]
            : reader.Prefix.BitCount + reader.Prefix.Bytes[0] + reader.LeftHash.Bytes[0] + reader.RightHash.Bytes[0] + reader.LeftKey[0] + reader.RightKey[0]);
    }

    [Test]
    public void Current_key_derivation_emits_exact_zone_lengths()
    {
        byte[] address32 = new byte[32];
        address32[0] = 0xA5;
        PbtPath account = Eip8297KeyDerivation.AccountKey(address32, 0);
        PbtStorageTreeKey headerStorage = Eip8297KeyDerivation.StorageKey(address32, new Nethermind.Int256.UInt256(63));
        PbtStorageTreeKey overflowStorage = Eip8297KeyDerivation.StorageKey(address32, new Nethermind.Int256.UInt256(64));
        PbtPath firstGroupCode = Eip8297KeyDerivation.OverflowCodeKey(Value(9), 5);
        PbtPath code = Eip8297KeyDerivation.OverflowCodeKey(Value(9), 300);
        byte[] expectedAddressHash = Hash(address32);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Length, Is.EqualTo(34));
            Assert.That(account.Bytes.Slice(1, 32).ToArray(), Is.EqualTo(expectedAddressHash));
            Assert.That(account.Bytes[0], Is.EqualTo(0));
            Assert.That(headerStorage.Length, Is.EqualTo(34));
            Assert.That(overflowStorage.Length, Is.EqualTo(66));
            Assert.That(overflowStorage.Bytes[0], Is.EqualTo(0xFF));
            Assert.That(firstGroupCode.Length, Is.EqualTo(34));
            Assert.That(firstGroupCode.Bytes[0], Is.EqualTo(1));
            Assert.That(firstGroupCode.Bytes[^1], Is.EqualTo(5));
            Assert.That(code.Length, Is.EqualTo(34));
            Assert.That(code.Bytes[0], Is.EqualTo(1));
            Assert.That(code.Bytes[^1], Is.EqualTo(44));
        }
    }

    [Test]
    public void Accumulated_partition_fold_matches_set_fold_and_oracle([Values] bool persisted)
    {
        List<(byte[] Key, byte[]? Value)> writes = [];
        EipReferenceTree oracle = new();
        foreach (byte zone in new byte[] { 0x00, 0x01, 0xFF })
            foreach (byte shard in new byte[] { 0x00, 0x01, 0xF0, 0xFF })
            {
                byte[] key = ZoneKey($"{zone:X2}");
                key[1] = shard;
                writes.Add((key, Value(1)));
                oracle.Insert(key, Value(1));
            }
        using PbtNodeGroupStore accumulatedStore = new();
        using PbtNodeGroupStore setStore = new();
        ValueHash256 initialRoot = persisted ? accumulatedStore.Fold(default, writes) : default;
        if (persisted) setStore.Fold(default, writes);
        using PbtWriteBatchBuilder<PbtPath> account = new(2);
        using PbtWriteBatchBuilder<PbtPath> code = new(2);
        using PbtWriteBatchBuilder<PbtStoragePath> storage = new(2);
        foreach ((byte[] key, byte[]? value) in writes)
        {
            switch (key[0])
            {
                case 0x00:
                    account.SetLeaf(new PbtPath(key), null);
                    account.SetLeaf(new PbtPath(key), new ValueHash256(value!));
                    break;
                case 0x01:
                    code.SetLeaf(new PbtPath(key), null);
                    code.SetLeaf(new PbtPath(key), new ValueHash256(value!));
                    break;
                case 0xFF:
                    storage.SetLeaf(new PbtStoragePath(key), null);
                    storage.SetLeaf(new PbtStoragePath(key), new ValueHash256(value!));
                    break;
            }
        }
        using PbtPartitionBatches accumulated = new() { Account = account.Build(), Code = code.Build(), Storage = storage.Build() };
        ValueHash256 accumulatedRoot = TrieUpdater.UpdateRoot(accumulatedStore, initialRoot, accumulated, PbtTreeHarness.FoldQuota(), FoldFanOut.Default, PbtPrefixlessBranchOmission.Interior, null);
        ValueHash256 setRoot = setStore.Fold(initialRoot, writes);
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(accumulatedStore.ExportPhysicalPayloads());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accumulatedRoot, Is.EqualTo(setRoot));
            Assert.That(accumulatedRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(reopened.Fold(accumulatedRoot, writes), Is.EqualTo(accumulatedRoot));
        }
    }

    [Test]
    public void Mixed_partition_batches_match_across_fan_outs_and_reopen(
        [Values(0, 1, 2, 3, 4, 16, 17, 31, 32, 33, 256)] int count,
        [Values(1, 16, 256)] int shards)
    {
        using PbtNodeGroupStore defaultStore = new();
        using PbtNodeGroupStore fannedOutStore = new();
        EipReferenceTree oracle = new();
        byte[][] keys = new byte[count][];
        for (int index = 0; index < count; index++)
        {
            byte[] key = ZoneKey(index % 3 == 2 ? "FF" : $"{index % 3:X2}");
            key[1] = (byte)(index % shards);
            key[^3] = (byte)(index >> 8);
            key[^2] = (byte)index;
            key[^1] = 1;
            keys[index] = key;
        }

        ValueHash256 defaultRoot = default;
        ValueHash256 fannedOutRoot = default;
        for (int round = 0; round < 3; round++)
        {
            List<(byte[] Key, byte[]? Value)> writes = [];
            for (int index = count - 1; index >= 0; index--)
            {
                // Leave one zone untouched during the mixed update, then remove everything.
                if (round == 1 && index % 3 == 1) continue;
                writes.Add((keys[index], Value(1)));
                writes.Add((keys[index], null));
                if (round == 0 || (round == 1 && index % 2 == 0))
                    writes.Add((keys[index], Value((byte)(round + 2))));
            }
            if (round == 1)
            {
                byte[] absentKey = ZoneKey("00");
                absentKey[^1] = 0xFF;
                writes.Add((absentKey, null));
            }

            ApplyOracle(oracle, writes);
            defaultRoot = defaultStore.Fold(defaultRoot, writes);
            fannedOutRoot = fannedOutStore.Fold(fannedOutRoot, writes, PbtPrefixlessBranchOmission.Interior, PbtTreeHarness.FanOut(1), null);
            using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(defaultStore.ExportPhysicalPayloads());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(defaultRoot, Is.EqualTo(fannedOutRoot), $"round {round}");
                Assert.That(defaultRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"round {round}");
                Assert.That(PhysicalRecords(defaultStore.ExportPhysicalPayloads()), Is.EqualTo(PhysicalRecords(fannedOutStore.ExportPhysicalPayloads())), $"round {round}");
                Assert.That(reopened.Fold(defaultRoot, writes), Is.EqualTo(defaultRoot), "replay after reopen");
            }
        }
    }

    [TestCase(17)]
    [TestCase(33)]
    public void Single_bucket_prefix_survives_existing_sibling_branches(int count)
    {
        List<(byte[] Key, byte[]? Value)> initial = BoundaryChanges(count, 40, 16, 2);
        initial.Add((ZoneKey("00AA8000000000"), Value(1)));
        initial.Add((ZoneKey("00AAAA80000000"), Value(2)));
        initial.Add((ZoneKey("00AAAAAA800000"), Value(3)));
        List<(byte[] Key, byte[]? Value)> changes = [];
        for (int index = 0; index < count; index++) changes.Add((initial[index].Key, Value(0xEF)));
        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        ApplyAll(bulk, serial, oracle, initial);
        ApplyAll(bulk, serial, oracle, changes);
        AssertEquivalentAfterReopen(bulk, serial, oracle, "single bucket with shallower siblings");
        CountingPbtStore store = new();
        ValueHash256 root = store.Fold(default, initial);
        root = store.Fold(root, changes);
        Assert.That(root, Is.EqualTo(bulk.RootHash));
        AssertAllMemoryReleased(store);
    }

    [Test]
    public void Boundary_bucketization_batches_match_oracle_serial_and_reopen(
        [Values(0, 1, 2, 3, 4, 16, 31, 32, 33, 256)] int count,
        [Values(0, 4, 20)] int groupDepth)
    {
        foreach (int occupiedSlots in new[] { 1, 2, 16 })
        {
            using PbtTreeHarness bulk = new();
            using PbtTreeHarness serial = new();
            EipReferenceTree oracle = new();
            List<(byte[] Key, byte[]? Value)> initial = BoundaryChanges(count, groupDepth, occupiedSlots, 0);
            ApplyAll(bulk, serial, oracle, initial);
            AssertEquivalentAfterReopen(bulk, serial, oracle, "build boundary batch");
            ApplyAll(bulk, serial, oracle, [(ZoneKey("00555555000002"), Value(0x77))]);

            List<(byte[] Key, byte[]? Value)> changes = BoundaryChanges(count, groupDepth, occupiedSlots, 2);
            for (int index = 0; index < changes.Count; index++)
            {
                byte[] key = changes[index].Key;
                if (index % 4 >= 2)
                {
                    key = (byte[])key.Clone();
                    key[^1] = 1;
                }
                changes[index] = (key, index % 2 == 0 ? null : Value(0xEF));
            }
            ApplyAll(bulk, serial, oracle, changes);
            AssertEquivalentAfterReopen(bulk, serial, oracle, "mixed boundary batch");

            List<(byte[] Key, byte[]? Value)> deletions = [];
            foreach ((byte[] key, byte[]? _) in initial) deletions.Add((key, null));
            foreach ((byte[] key, byte[]? _) in changes) deletions.Add((key, null));
            ApplyAll(bulk, serial, oracle, deletions);
            AssertEquivalentAfterReopen(bulk, serial, oracle, "boundary buckets collapse to untouched survivor");
            ApplyAll(bulk, serial, oracle, initial);
            AssertEquivalentAfterReopen(bulk, serial, oracle, "restore boundary buckets after collapse");
        }
    }

    private static List<(byte[] Key, byte[]? Value)> BoundaryChanges(int count, int groupDepth, int occupiedSlots, int order)
    {
        List<(byte[] Key, byte[]? Value)> changes = [];
        for (int index = 0; index < count; index++)
        {
            // The group depth counts from below the zone byte.
            byte[] key = ZoneKey("00AAAAAA000000");
            int destination = occupiedSlots == 1 ? 15 : occupiedSlots == 2 ? (index % 2) * 15 : index % 16;
            int shift = 4 - groupDepth % 8;
            key[1 + groupDepth / 8] = (byte)((key[1 + groupDepth / 8] & ~(15 << shift)) | (destination << shift));
            key[4] = (byte)(index >> 8);
            key[5] = (byte)index;
            changes.Add((key, Value((byte)(index + 1))));
        }
        changes.Sort((left, right) => left.Key.AsSpan().SequenceCompareTo(right.Key));
        if (order == 1) changes.Reverse();
        if (order == 2)
        {
            Random random = new(8297);
            for (int index = changes.Count - 1; index > 0; index--)
            {
                int destination = random.Next(index + 1);
                (changes[index], changes[destination]) = (changes[destination], changes[index]);
            }
        }
        return changes;
    }

    [Test]
    public void Shuffled_writes_cover_all_first_group_boundary_destinations()
    {
        int[] order = [7, 15, 2, 10, 0, 12, 5, 1, 9, 14, 3, 8, 6, 11, 4, 13];
        List<(byte[] Key, byte[]? Value)> changes = [];
        foreach (int destination in order)
            changes.Add((AccountKey((byte)(destination << 4), (byte)(0x20 + destination)), Value((byte)(destination + 1))));

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
            byte[] key = AccountKey((byte)(0xA0 | destination), 0x11);
            initial.Add((key, Value((byte)(destination + 1))));
            changes.Add((key, Value((byte)(0x40 + destination))));
        }
        initial.Add((AccountKey(0xB1, 0x11), Value(0xEE)));
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
    public void All_second_group_boundary_destinations_fetch_only_touched_groups()
    {
        CountingPbtStore store = new();
        List<(byte[] Key, byte[]? Value)> initial = [];
        List<(byte[] Key, byte[]? Value)> changes = [];
        for (int destination = 0; destination < 16; destination++)
        {
            byte[] key = AccountKey((byte)(0xA0 | destination), 0x11);
            initial.Add((key, Value((byte)(destination + 1))));
            changes.Add((key, Value((byte)(0x40 + destination))));
        }
        initial.Add((AccountKey(0xB1, 0x11), Value(0xEE)));

        ValueHash256 root = store.Fold(default, initial);
        store.ResetReads();
        ValueHash256 changedRoot = store.Fold(root, changes);
        PbtStorageNodePath untouchedGroup = new(Bytes.FromHexString("00B0"), 12);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changedRoot, Is.Not.EqualTo(root));
            Assert.That(store.GroupReads.ContainsKey(untouchedGroup), Is.False);
        }
    }

    private static IEnumerable<TestCaseData> DenseGroupCollapseCases()
    {
        int[] groupDepths = [8, 12, 248, 252, 520, 524];
        int[] retainedMasks = [0x0000, 0x0001, 0x8000, 0x000A, 0xA000, 0xA55A, 0x8001, 0xFFFF];
        foreach (int groupDepth in groupDepths)
            foreach (int retainedMask in retainedMasks)
                yield return new TestCaseData(groupDepth, retainedMask)
                    .SetName($"Dense_group_paths_survive_collapse_and_restoration(depth={groupDepth}, retained=0x{retainedMask:X4})");
    }

    [TestCaseSource(nameof(DenseGroupCollapseCases))]
    public void Dense_group_paths_survive_collapse_and_restoration(int groupDepth, int retainedMask)
    {
        byte[] sharedKey = ZoneKey(groupDepth + 4 <= PbtPath.KeyLength * 8 ? "00" : "FF");
        new Random(8297).NextBytes(sharedKey.AsSpan(1));
        List<(byte[] Key, byte[]? Value)> initial = [];
        List<(byte[] Key, byte[]? Value)> deletions = [];
        for (int slot = 0; slot < PbtFourLevelGroupGeometry.BoundarySlots; slot++)
        {
            byte[] key = (byte[])sharedKey.Clone();
            int shift = 4 - (groupDepth & 4);
            key[groupDepth >> 3] = (byte)((key[groupDepth >> 3] & ~(0xF << shift)) | (slot << shift));
            initial.Add((key, Value((byte)(slot + 1))));
            if ((retainedMask & (1 << slot)) == 0) deletions.Add((key, null));
        }

        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        ApplyAll(bulk, serial, oracle, initial);
        AssertEquivalentAfterReopen(bulk, serial, oracle, "dense group");
        ApplyAll(bulk, serial, oracle, deletions);
        AssertEquivalentAfterReopen(bulk, serial, oracle, "collapsed group");
        ApplyAll(bulk, serial, oracle, initial);
        AssertEquivalentAfterReopen(bulk, serial, oracle, "restored group");
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(11)]
    [TestCase(12)]
    [TestCase(13)]
    public void Span_partition_divergence_before_on_and_after_compressed_group_boundaries_matches_oracle(int divergenceBit)
    {
        byte[] leftKey = ZoneKey("000000");
        byte[] existingRightKey = ZoneKey("000008");
        byte[] insertedKey = ZoneKey("000000");
        // The divergence bit counts from below the zone byte.
        int keyBit = divergenceBit + 8;
        insertedKey[keyBit >> 3] |= (byte)(1 << (7 - (keyBit & 7)));
        int trailingBit = keyBit == 20 ? 21 : keyBit + 1;
        insertedKey[trailingBit >> 3] |= (byte)(1 << (7 - (trailingBit & 7)));
        (byte[] Key, byte[]? Value)[] initial = [(leftKey, Value(1)), (existingRightKey, Value(2))];
        (byte[] Key, byte[]? Value)[] changes = [(insertedKey, Value(3)), (existingRightKey, Value(4))];

        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        ApplyAll(bulk, serial, oracle, [.. initial]);
        ApplyAll(bulk, serial, oracle, [.. changes]);
        AssertEquivalentAfterReopen(bulk, serial, oracle, $"divergence bit {divergenceBit}");
        ApplyAll(bulk, serial, oracle, [(insertedKey, null), (leftKey, Value(5)), (existingRightKey, null)]);
        AssertEquivalentAfterReopen(bulk, serial, oracle, $"collapse divergence bit {divergenceBit}");
        ApplyAll(bulk, serial, oracle, [.. changes]);
        AssertEquivalentAfterReopen(bulk, serial, oracle, $"restore divergence bit {divergenceBit}");
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
    public void Mixed_batch_produces_only_the_remaining_trie_leaf(bool deleteKeyExists)
    {
        CountingPbtStore store = new();
        byte[] deleteKeyBytes = AccountKey(0x00);
        byte[] setKeyBytes = AccountKey(0x80);
        ValueHash256 root = deleteKeyExists
            ? store.Fold(default, [(deleteKeyBytes, Value(1))])
            : default;

        ValueHash256 rootAfterUpdate = store.Fold(root, [(deleteKeyBytes, null), (setKeyBytes, Value(2))]);

        byte[] expectedLeaf = PbtNodeCodec.EncodeLeaf(new PbtStorageTreeKey(setKeyBytes));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootAfterUpdate, Is.EqualTo(PbtNodeCodec.HashLeaf(setKeyBytes, Value(2))));
            Assert.That(store.Inner.EnumerateRecords(), Has.Count.EqualTo(1));
            Assert.That(store.GetNode(new PbtStorageNodePath([], 0), rootAfterUpdate), Is.EqualTo(expectedLeaf));
        }
    }

    [Test]
    public void Shared_operation_prefix_across_depth_jumps_matches_reference(
        [Values(3, 4, 7, 8, 13, 128, 260)] int divergenceBit,
        [Values(false, true)] bool persisted)
    {
        byte[] leftKey = AccountKey();
        byte[] rightKey = AccountKey();
        // The divergence bit counts from below the zone byte.
        int keyBit = divergenceBit + 8;
        rightKey[keyBit >> 3] = (byte)(1 << (7 - (keyBit & 7)));
        byte[] untouchedKey = AccountKey(0x80);
        List<(byte[] Key, byte[]? Value)> initial = persisted
            ? [(leftKey, Value(1)), (rightKey, Value(2)), (untouchedKey, Value(3))]
            : [];
        List<(byte[] Key, byte[]? Value)> changes = [(leftKey, Value(4)), (rightKey, Value(5))];
        CountingPbtStore store = new();
        ValueHash256 root = store.Fold(default, initial);
        root = store.Fold(root, changes);

        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        ApplyAll(bulk, serial, oracle, initial);
        ApplyAll(bulk, serial, oracle, changes);
        AssertEquivalentAfterReopen(bulk, serial, oracle, $"prefix at bit {divergenceBit}, persisted {persisted}");
        Assert.That(root, Is.EqualTo(bulk.RootHash));
        AssertAllMemoryReleased(store);
    }

    [Test]
    public void Empty_batch_returns_supplied_root_without_storage_access()
    {
        CountingPbtStore store = new();
        ValueHash256 suppliedRoot = new(Value(0xEE));
        ValueHash256 result = store.Fold(suppliedRoot, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(suppliedRoot));
            Assert.That(store.Reads, Is.Zero);
            Assert.That(store.Applies, Is.Zero);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Non_empty_update_discovers_persisted_root_instead_of_using_stale_current_root(bool useDefaultRoot)
    {
        using PbtNodeGroupStore source = new();
        ValueHash256 actualRoot = source.Fold(default, [
            (AccountKey(0x12), Value(1)), (AccountKey(0x92), Value(2)), (AccountKey(0xF0), Value(3))]);
        PbtPhysicalPayload[] initialPayloads = [.. source.ExportPhysicalPayloads()];
        ValueHash256 staleRoot = useDefaultRoot ? default : new ValueHash256(Value(0xEE));
        (byte[] Key, byte[]? Value)[] changes = [(AccountKey(0x12), Value(4)), (AccountKey(0xA0), Value(5))];

        using PbtNodeGroupStore staleStore = PbtNodeGroupStore.FromPhysicalPayloads(initialPayloads);
        using PbtNodeGroupStore actualStore = PbtNodeGroupStore.FromPhysicalPayloads(initialPayloads);
        ValueHash256 resultFromStaleRoot = staleStore.Fold(staleRoot, changes);
        ValueHash256 resultFromActualRoot = actualStore.Fold(actualRoot, changes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resultFromStaleRoot, Is.EqualTo(resultFromActualRoot));
            Assert.That(CanonicalRecords(staleStore), Is.EqualTo(CanonicalRecords(actualStore)));
            Assert.That(PhysicalRecords(staleStore.ExportPhysicalPayloads()), Is.EqualTo(PhysicalRecords(actualStore.ExportPhysicalPayloads())));
        }
    }

    [Test]
    public void Same_group_recursion_uses_one_frame_and_publishes_unchanged_group()
    {
        CountingPbtStore store = new();
        (byte[] Key, byte[]? Value)[] initial = [(AccountKey(0x00), Value(1)), (AccountKey(0x40), Value(2))];
        ValueHash256 root = store.Fold(default, initial);
        store.ResetReads();

        ValueHash256 unchangedRoot = store.Fold(root, initial);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unchangedRoot, Is.EqualTo(root));
            Assert.That(store.Reads, Is.EqualTo(1), "same-group logical nodes use the active frame");
            Assert.That(store.Applies, Is.EqualTo(2));
        }

        AssertAllMemoryReleased(store);
    }

    [Test]
    public void Boundary_crossing_fetches_only_visited_groups_once()
    {
        CountingPbtStore store = new();
        ValueHash256 root = store.Fold(default, [
            (AccountKey(0x00), Value(1)), (AccountKey(0x08), Value(2)), (AccountKey(0x80), Value(3)), (AccountKey(0x88), Value(4))]);
        store.ResetReads();

        store.Fold(root, [(AccountKey(0x00), Value(5))]);

        PbtStorageNodePath absentGroup = new(Bytes.FromHexString("0000"), 12);
        PbtStorageNodePath untouchedGroup = new(Bytes.FromHexString("0080"), 12);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.GroupReads.Keys, Is.EquivalentTo(new PbtStorageNodePath[] { new([], 0), new(Bytes.FromHexString("00"), 8) }),
                "only the root group and the zone's group at depth eight; the left boundary's group is never entered");
            Assert.That(store.GroupReads.Values, Has.All.EqualTo(1));
            Assert.That(store.GroupReads.ContainsKey(absentGroup), Is.False, "the changed left boundary inlines its leaves, so its group is absent and never fetched");
            Assert.That(store.GroupReads.ContainsKey(untouchedGroup), Is.False, "the untouched right group is not fetched");
        }
    }

    // Each case leaves the group at 0x0000 with a boundary node that owns nothing below it, so the fold that
    // enters it must publish it without ever reading it.
    [TestCase(new byte[] { 0x80 }, new byte[] { 0x00, 0x08 }, TestName = "Absent_group_is_never_fetched_over_an_empty_boundary")]
    [TestCase(new byte[] { 0x80, 0x00 }, new byte[] { 0x04 }, TestName = "Absent_group_is_never_fetched_over_a_leaf_boundary")]
    [TestCase(new byte[] { 0x80, 0x00, 0x08 }, new byte[] { 0x00 }, TestName = "Absent_group_is_never_fetched_over_two_inlined_leaves")]
    public void Absent_group_is_never_fetched(byte[] initialKeys, byte[] changedKeys)
    {
        CountingPbtStore store = new();
        using PbtTreeHarness expected = new();
        (byte[] Key, byte[]? Value)[] initial = ToChanges(initialKeys, marker: 0);
        (byte[] Key, byte[]? Value)[] changes = ToChanges(changedKeys, marker: 1);
        ValueHash256 root = store.Fold(default, initial);
        expected.ApplyBatch(initial);
        store.ResetReads();

        ValueHash256 result = store.Fold(root, changes);

        PbtStorageNodePath absentGroup = new(Bytes.FromHexString("0000"), 12);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected.ApplyBatch(changes)));
            Assert.That(store.GroupReads.ContainsKey(absentGroup), Is.False, "a group that stores nothing is never fetched");
            Assert.That(PhysicalRecords(store.Inner.ExportPhysicalPayloads()), Is.EqualTo(PhysicalRecords(expected)));
            PbtStoreTestExtensions.AssertSubtreeBytes(store.Inner.ExportPhysicalPayloads());
        }

        static (byte[] Key, byte[]? Value)[] ToChanges(byte[] keys, byte marker)
        {
            (byte[] Key, byte[]? Value)[] changes = new (byte[], byte[]?)[keys.Length];
            for (int index = 0; index < keys.Length; index++) changes[index] = (AccountKey(keys[index]), Value((byte)(keys[index] + marker)));
            return changes;
        }
    }

    [TestCase(false, 0)]
    [TestCase(false, 1)]
    [TestCase(true, 0)]
    public void Group_leases_are_released_when_decode_or_apply_fails(bool applyFailure, int malformedPayloadLength)
    {
        CountingPbtStore store = new();
        ValueHash256 root = store.Fold(default, [(AccountKey(0x12), Value(1))]);
        int appliesBeforeFailure = store.Applies;
        if (applyFailure) store.ThrowOnApply = true;
        else
        {
            store.OverrideGroup = (_, _) =>
            {
                RefCountingMemory memory = store.MemoryProvider.Rent(malformedPayloadLength);
                if (malformedPayloadLength != 0) memory.GetSpan()[0] = 0x01;
                return memory;
            };
        }

        TrackingMemoryProvider memoryProvider = new();
        Action update = () => store.Fold(root, [(AccountKey(0x12), Value(2))], PbtPrefixlessBranchOmission.Interior, FoldFanOut.Default, memoryProvider);
        if (applyFailure) Assert.Throws<InvalidOperationException>(update);
        else Assert.Catch(update);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Applies, Is.EqualTo(appliesBeforeFailure + (applyFailure ? 1 : 0)));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        }

        AssertAllMemoryReleased(store);
    }

    [Test]
    public void Group_lease_is_released_when_traversal_finds_a_missing_node()
    {
        CountingPbtStore store = new();
        // [0x13] keeps a stored branch below the root for the override to hide.
        ValueHash256 root = store.Fold(default, [(AccountKey(0x12), Value(1)), (AccountKey(0x92), Value(2)), (AccountKey(0x13), Value(4))]);
        int appliesBeforeFailure = store.Applies;
        store.OverrideNode = path => path.BitDepth == 0 ? store.Inner.GetNode(path) : null;

        TrackingMemoryProvider memoryProvider = new();
        Assert.Throws<InvalidDataException>(() => store.Fold(root, [(AccountKey(0x12), Value(3))], PbtPrefixlessBranchOmission.Interior, FoldFanOut.Default, memoryProvider));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Applies, Is.EqualTo(appliesBeforeFailure));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        }

        AssertAllMemoryReleased(store);
    }

    [Test]
    public void Non_byte_aligned_prefix_split_and_sibling_promotion_round_trip()
    {
        PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        byte[] first = AccountKey(0xAA, 0x00);
        byte[] second = AccountKey(0xAB, 0x00);
        byte[] split = AccountKey(0xA8, 0x00);
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
    public void Failed_batches_never_apply_or_change_state_when_a_referenced_node_is_missing()
    {
        CountingPbtStore store = new();
        ValueHash256 root = store.Fold(default, [(AccountKey(0x12), Value(1)), (AccountKey(0x92), Value(2)), (AccountKey(0x13), Value(4))]);
        PbtPhysicalPayload[] before = [.. store.Inner.ExportPhysicalPayloads()];
        int appliesBeforeFailure = store.Applies;
        store.OverrideNode = path => path.BitDepth == 0 ? store.Inner.GetNode(path) : null;

        Assert.Throws<InvalidDataException>(() => store.Fold(root, [(AccountKey(0x12), Value(3))]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Applies, Is.EqualTo(appliesBeforeFailure));
            Assert.That(PhysicalRecords(store.Inner.ExportPhysicalPayloads()), Is.EqualTo(PhysicalRecords(before)));
        }

        AssertAllMemoryReleased(store);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Group_outputs_retain_only_store_leases_after_mutations_and_partition_folds(bool parallel)
    {
        TrackingMemoryProvider memoryProvider = new() { FillByte = 0xFF };
        using PbtNodeGroupStore store = new();
        using PbtNodeGroupStore expectedStore = new();
        EipReferenceTree oracle = new();
        ValueHash256 root = default;
        ValueHash256 expectedRoot = default;
        byte[][] keys = [ZoneKey("00123450"), ZoneKey("00123458"), ZoneKey("00123800"), ZoneKey("01123450"), ZoneKey("ff123450")];
        (byte[] Key, byte[]? Value)[][] batches =
        [
            [(keys[0], Value(1)), (keys[1], Value(2)), (keys[2], Value(3)), (keys[3], Value(4)), (keys[4], Value(5))],
            [],
            [(keys[0], Value(1))],
            [(keys[1], Value(6)), (keys[2], null)],
            [(keys[0], null)],
            [(keys[1], null), (keys[3], null), (keys[4], null)],
        ];
        foreach ((byte[] Key, byte[]? Value)[] changes in batches)
        {
            root = ApplyTracked(store, root, changes, parallel, memoryProvider);
            expectedRoot = expectedStore.Fold(expectedRoot, changes);
            foreach ((byte[] key, byte[]? value) in changes)
            {
                if (value is null) oracle.Delete(key);
                else oracle.Insert(key, value);
            }
            using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.ExportPhysicalPayloads());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root, Is.EqualTo(expectedRoot));
                Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
                Assert.That(PhysicalRecords(reopened.ExportPhysicalPayloads()), Is.EqualTo(PhysicalRecords(expectedStore.ExportPhysicalPayloads())));
                AssertOnlyPublishedRentalsRemain(store, memoryProvider);
            }
        }
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        Assert.That(memoryProvider.RentCount, Is.GreaterThan(0));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Owned_node_encodings_are_released_at_every_rent_failure(bool parallel)
    {
        (byte[] Key, byte[]? Value)[] changes =
        [
            (ZoneKey("00123450"), Value(1)), (ZoneKey("00123458"), Value(2)),
            (ZoneKey("01123450"), Value(3)), (ZoneKey("01123458"), Value(4)),
            (ZoneKey("ff123450"), Value(5)), (ZoneKey("ff123458"), Value(6)),
        ];
        TrackingMemoryProvider successfulProvider = new();
        using (PbtNodeGroupStore store = new()) ApplyTracked(store, default, changes, parallel, successfulProvider);
        Assert.That(TrackingMemoryProvider.CountUnreleased(successfulProvider.Rented), Is.Zero);
        for (int rent = 1; rent <= successfulProvider.RentCount; rent++)
        {
            TrackingMemoryProvider memoryProvider = new() { ThrowOnRent = rent };
            using PbtNodeGroupStore store = new();
            Exception? exception = Assert.Catch(() => ApplyTracked(store, default, changes, parallel, memoryProvider));
            if (exception is AggregateException aggregateException)
                Assert.That(aggregateException.Flatten().InnerExceptions, Has.All.TypeOf<InvalidOperationException>());
            else
                Assert.That(exception, Is.TypeOf<InvalidOperationException>());
            AssertOnlyPublishedRentalsRemain(store, memoryProvider);
            store.Dispose();
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero, $"failed rental {rent}");
        }
    }

    [TestCase(false, 0)]
    [TestCase(false, 4)]
    [TestCase(false, 8)]
    [TestCase(true, 0)]
    [TestCase(true, 4)]
    [TestCase(true, 8)]
    public void Owned_node_encodings_are_released_when_worker_or_ancestor_publish_fails(bool parallel, int failedDepth)
    {
        TrackingMemoryProvider memoryProvider = new();
        TrackingMemoryProvider storeProvider = new();
        using PbtNodeGroupStore innerStore = new(storeProvider);
        FailingPublishStore store = new(innerStore, failedDepth);
        (byte[] Key, byte[]? Value)[] changes =
        [
            (ZoneKey("00000000"), Value(1)), (ZoneKey("00800000"), Value(2)),
            // Branches below the zone boundary, so the zone's group at depth eight is stored and published.
            (ZoneKey("00400000"), Value(7)),
            (ZoneKey("01000000"), Value(3)), (ZoneKey("01800000"), Value(4)),
            (ZoneKey("ff000000"), Value(5)), (ZoneKey("ff800000"), Value(6)),
        ];
        Assert.Catch(() => ApplyTracked(store, default, changes, parallel, memoryProvider));
        AssertOnlyPublishedRentalsRemain(innerStore, memoryProvider);
        innerStore.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        Assert.That(TrackingMemoryProvider.CountUnreleased(storeProvider.Rented), Is.Zero);
    }

    private sealed class FailingPublishStore(IPbtStore store, int failedDepth) : IPbtStore, IPbtNodeGroupSink
    {
        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash) => store.GetNodeGroup(groupKey, hash);
        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash, RefCountingMemory? payload)
        {
            if (groupKey.BitDepth == failedDepth) throw new InvalidOperationException("Configured publish failure.");
            store.SetNodeGroup(groupKey, hash, payload);
        }
    }

    [Test]
    public void Promoted_subtree_is_materialized_before_its_frame_is_disposed()
    {
        CountingPbtStore store = new();
        // The two 0012345x keys keep a stored branch in the group at depth 28; deleting 00123458 promotes it.
        ValueHash256 root = store.Fold(default, [
            (ZoneKey("00123450"), Value(1)), (ZoneKey("00123451"), Value(4)), (ZoneKey("00123458"), Value(2)), (ZoneKey("0080"), Value(3))]);
        TrackingMemoryProvider readProvider = new();
        TrackingMemoryProvider nodeProvider = new() { FillByte = 0xFF };
        RefCountingMemory? promotedPayload = null;
        bool checkedPromotion = false;
        store.OverrideGroup = (groupKey, hash) =>
        {
            using RefCountingMemory? stored = store.Inner.GetNodeGroup(groupKey, hash);
            if (stored is null) return null;
            RefCountingMemory read = readProvider.Rent(stored.Memory.Length);
            stored.GetSpan().CopyTo(read.GetSpan());
            if (groupKey.BitDepth == 28) promotedPayload = read;
            return read;
        };
        store.OnApply = groupKey =>
        {
            if (groupKey.BitDepth != 0) return;
            Assert.That(promotedPayload, Is.Not.Null);
            Assert.That(TrackingMemoryProvider.CountUnreleased([promotedPayload!]), Is.Zero);
            checkedPromotion = true;
        };
        root = store.Fold(root, [(ZoneKey("00123458"), null)], PbtPrefixlessBranchOmission.Interior, FoldFanOut.Default, nodeProvider);
        EipReferenceTree oracle = new();
        oracle.Insert(ZoneKey("00123450"), Value(1));
        oracle.Insert(ZoneKey("00123451"), Value(4));
        oracle.Insert(ZoneKey("0080"), Value(3));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(checkedPromotion, Is.True);
            Assert.That(TrackingMemoryProvider.CountUnreleased(readProvider.Rented), Is.Zero);
            AssertOnlyPublishedRentalsRemain(store.Inner, nodeProvider, false);
        }
        AssertAllMemoryReleased(store);
        Assert.That(TrackingMemoryProvider.CountUnreleased(nodeProvider.Rented), Is.Zero);
    }

    [Test]
    public void Ordered_group_emission_rents_one_bucket_instead_of_per_node([Values(2, 8)] int leafCount)
    {
        // Every leaf pair is inlined in a stored branch. A single pair is the root, whose prefix spans the zone byte;
        // otherwise the pairs are the prefixless branches of the zone's group at depth eight, below a root of their own.
        bool singlePair = leafCount == 2;
        int pairBranchLength = PbtNodeCodec.BranchLength(singlePair ? 8 : 0, PbtPath.KeyLength, PbtPath.KeyLength);
        int storedNodes = leafCount / 2;
        TrackingMemoryProvider provider = new() { FillByte = 0xFF };
        using PbtNodeGroupStore store = new();
        EipReferenceTree oracle = new();
        (byte[] Key, byte[]? Value)[] changes = new (byte[], byte[]?)[leafCount];
        for (int index = 0; index < leafCount; index++)
        {
            byte[] key = ZoneKey($"00{index * 256 / leafCount:X2}");
            changes[index] = (key, Value((byte)(index + 1)));
            oracle.Insert(key, changes[index].Value!);
        }
        ValueHash256 root = ApplyTracked(store, default, changes, false, provider);
        IReadOnlyList<PbtPhysicalPayload> payloads = store.ExportPhysicalPayloads();
        Assert.That(payloads, Has.Count.EqualTo(singlePair ? 1 : 2));
        PbtStorageNodePath groupKey = singlePair ? new([], 0) : new(Bytes.FromHexString("00"), 8);
        PbtPhysicalPayload group = payloads.Single(payload => payload.Key.Equals(groupKey));
        PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(groupKey, group.Payload.Span);
        List<PbtNodeRecord> records = [];
        PbtNodeGroupReader.Enumerator nodes = reader.EnumerateNodes();
        while (nodes.MoveNext())
            records.Add(new(PbtFourLevelGroupGeometry.PathOf(groupKey, nodes.CurrentPosition), nodes.Current));
        byte[] expectedPayload = new byte[group.Payload.Length];
        BufferWriter writer = new(expectedPayload);
        PbtNodeGroupEncoder.Encode(ref writer, groupKey, records, default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(reader.Count, Is.EqualTo(storedNodes));
            Assert.That(group.Payload.Length, Is.EqualTo(PbtNodeGroupCodec.HeaderLength + storedNodes * pairBranchLength
                + storedNodes * sizeof(ushort) + sizeof(uint) + PbtNodeGroupCodec.DescendantMaskLength));
            Assert.That(group.Payload.ToArray(), Is.EqualTo(expectedPayload));
            Assert.That(provider.RequestedLengths, Is.EquivalentTo(payloads.Select(static payload => payload.Payload.Length)),
                "every published group is rented once, at its final size");
            AssertOnlyPublishedRentalsRemain(store, provider);
        }
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(payloads);
        Assert.That(PhysicalRecords(reopened.ExportPhysicalPayloads()), Is.EqualTo(PhysicalRecords(payloads)));
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [TestCase("00AA00", "00AA80", "00B000", "00AA", 16)]
    [TestCase("00AAC0", "00AAE0", "00B000", "00AAC0", 18)]
    public void Ordered_emission_removes_old_branch_position_when_hoisting_to_root(
        string leftHex, string rightHex, string siblingHex, string prefixHex, int prefixBits)
    {
        using PbtNodeGroupStore store = new();
        byte[] left = ZoneKey(leftHex), right = ZoneKey(rightHex), sibling = ZoneKey(siblingHex);
        ValueHash256 root = store.Fold(default, [(left, Value(1)), (right, Value(2)), (sibling, Value(3))]);
        PbtStorageNodePath groupKey = new([], 0);
        PbtStorageNodePath branchGroupKey = new(Bytes.FromHexString("00"), 8);
        using (RefCountingMemory? original = store.GetPhysicalNodeGroup(branchGroupKey))
        {
            PbtNodeGroupReader reader = PbtStoreTestExtensions.ReadGroup(branchGroupKey, original!.GetSpan());
            PbtNodeReader branch = new(reader[18]);
            Assert.That(branch.Prefix.BitCount, Is.EqualTo(prefixBits - 12));
        }
        root = store.Fold(root, [(sibling, null)]);
        EipReferenceTree oracle = new();
        oracle.Insert(left, Value(1));
        oracle.Insert(right, Value(2));
        using RefCountingMemory? updated = store.GetPhysicalNodeGroup(groupKey);
        PbtNodeGroupReader updatedReader = PbtStoreTestExtensions.ReadGroup(groupKey, updated!.GetSpan());
        PbtNodeReader promoted = new(updatedReader[30]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(updatedReader.Count, Is.EqualTo(1));
            Assert.That(promoted.Prefix.BitCount, Is.EqualTo(prefixBits));
            Assert.That(promoted.Prefix.Bytes.ToArray(), Is.EqualTo(Bytes.FromHexString(prefixHex)));
        }
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.ExportPhysicalPayloads());
        Assert.That(reopened.Fold(root, [(left, Value(1))]), Is.EqualTo(root));
    }

    private static void AssertOnlyPublishedRentalsRemain(PbtNodeGroupStore store, TrackingMemoryProvider provider, bool allPublishedAreTracked = true)
    {
        HashSet<RefCountingMemory> published = [];
        foreach (PbtStorageNodePath groupKey in store.EnumerateNodeGroupKeys())
        {
            using RefCountingMemory? payload = store.GetPhysicalNodeGroup(groupKey);
            published.Add(payload!);
        }
        foreach (RefCountingMemory rental in provider.Rented)
            Assert.That(TrackingMemoryProvider.CountUnreleased([rental]), Is.EqualTo(published.Contains(rental) ? 1 : 0));
        if (allPublishedAreTracked)
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(published.Count));
    }

    private static ValueHash256 ApplyTracked(IPbtStore store, ValueHash256 root, (byte[] Key, byte[]? Value)[] changes, bool parallel, TrackingMemoryProvider memoryProvider) =>
        store.Fold(root, changes, PbtPrefixlessBranchOmission.Interior, parallel ? PbtTreeHarness.FanOut(1) : FoldFanOut.Default, memoryProvider);

    private static (List<(byte[] Key, byte[]? Value)> Initial, List<(byte[] Key, byte[]? Value)> Changes) Scenario(string name) => name switch
    {
        "insert-only" => ([], [(AccountKey(0x10), Value(1)), (AccountKey(0x20), Value(2))]),
        "delete-only" => ([(AccountKey(0x10), Value(1)), (AccountKey(0x20), Value(2))], [(AccountKey(0x10), null), (AccountKey(0xFF), null)]),
        "replacements" => ([(AccountKey(0x10), Value(1)), (AccountKey(0x20), Value(2))], [(AccountKey(0x10), Value(3)), (AccountKey(0x20), Value(4))]),
        "absent-deletes" => ([(AccountKey(0x10), Value(1))], [(AccountKey(0xFF), null), (AccountKey(0xEE), null)]),
        "duplicate-last-write-wins" => ([], [(AccountKey(0x10), Value(1)), (AccountKey(0x10), Value(2)), (AccountKey(0x10), null), (AccountKey(0x10), Value(3))]),
        "duplicate-final-delete" => ([(AccountKey(0x10), Value(1))], [(AccountKey(0x10), Value(2)), (AccountKey(0x10), null), (AccountKey(0x10), Value(3)), (AccountKey(0x10), null)]),
        "mixed-delete-set" => ([(AccountKey(0x10), Value(1)), (AccountKey(0x20), Value(2))], [(AccountKey(0x10), null), (AccountKey(0x30), Value(3)), (AccountKey(0x20), Value(4))]),
        "shuffled-mixed-delete-set" => ([(AccountKey(0x10), Value(1)), (AccountKey(0x20), Value(2)), (AccountKey(0x80), Value(3))],
            [(AccountKey(0x80), null), (AccountKey(0x21), Value(4)), (AccountKey(0x10), null), (AccountKey(0x20), Value(5)), (AccountKey(0x11), Value(6))]),
        "mixed-canonicalization-boundaries" => (
            [(AccountKey(0xA8, 0x00), Value(1)), (AccountKey(0xAA, 0x00), Value(2)), (AccountKey(0xAB, 0x00), Value(3)),
             (AccountKey(0xB0, 0x00), Value(4)), (AccountKey(0xB8, 0x00), Value(5)), (AccountKey(0xF0, 0x00), Value(6))],
            [(AccountKey(0xAA, 0x00), Value(20)), (AccountKey(0xAB, 0x00), null), (AccountKey(0xA9, 0x00), Value(7)),
             (AccountKey(0xA8, 0x00), null), (AccountKey(0xA8, 0x00), Value(8)), (AccountKey(0xA8, 0x00), null),
             (AccountKey(0xAC, 0x00), null), (AccountKey(0xB8, 0x00), null), (AccountKey(0xB4, 0x00), Value(9)),
             (AccountKey(0xF0, 0x00), Value(10)), (AccountKey(0xF0, 0x00), null), (AccountKey(0xF0, 0x00), Value(11))]),
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
        PbtStoreTestExtensions.AssertSubtreeBytes(bulk.PhysicalPayloads);
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
            if (value is null || new ValueHash256(value) == default) oracle.Delete(key);
            else oracle.Insert(key, value);
        }
    }

    private static string[] CanonicalRecords(PbtNodeGroupStore store)
    {
        List<string> records = [];
        foreach (PbtNodeRecord record in store.EnumerateRecords())
            records.Add(Convert.ToHexString(record.Path.ToEncodedArray()) + Convert.ToHexString(record.Encoding.Span));
        return [.. records];
    }

    private static string[] PhysicalRecords(PbtTreeHarness tree) => PhysicalRecords(tree.PhysicalPayloads);

    private static string[] PhysicalRecords(IEnumerable<PbtPhysicalPayload> payloads)
    {
        List<string> records = [];
        foreach (PbtPhysicalPayload payload in payloads)
            records.Add(Convert.ToHexString(payload.Key.ToEncodedArray()) + Convert.ToHexString(payload.Payload.Span));
        records.Sort(StringComparer.Ordinal);
        return [.. records];
    }

    private static string[] PhysicalRecords(PbtPhysicalPayload[] payloads) => PhysicalRecords((IEnumerable<PbtPhysicalPayload>)payloads);

    private static void AssertAllMemoryReleased(CountingPbtStore store)
    {
        store.Inner.Dispose();
        Assert.That(store.UnreleasedMemoryCount, Is.Zero);
    }

    private sealed class CountingPbtStore : IPbtStore, IPbtNodeGroupSink
    {
        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

        internal TrackingMemoryProvider MemoryProvider { get; } = new();
        internal PbtNodeGroupStore Inner { get; }

        internal CountingPbtStore() => Inner = new(MemoryProvider);

        internal int UnreleasedMemoryCount => TrackingMemoryProvider.CountUnreleased(MemoryProvider.Rented);
        internal int Reads { get; private set; }
        internal int Applies { get; private set; }
        internal Dictionary<PbtStorageNodePath, int> GroupReads { get; } = [];
        internal Func<PbtStorageNodePath, byte[]?>? OverrideNode { get; set; }
        internal Func<PbtStorageNodePath, ValueHash256, RefCountingMemory?>? OverrideGroup { get; set; }
        internal bool ThrowOnApply { get; set; }
        internal Action<PbtStorageNodePath>? OnApply { get; set; }

        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash)
        {
            Reads++;
            PbtStorageNodePath storageGroupKey = groupKey.ToPath<PbtStorageNodePath>();
            GroupReads[storageGroupKey] = GroupReads.GetValueOrDefault(storageGroupKey) + 1;
            if (OverrideGroup is { } overrideGroup)
            {
                RefCountingMemory? overriddenPayload = overrideGroup(storageGroupKey, hash);
                return overriddenPayload;
            }
            if (OverrideNode is { } overrideNode)
            {
                List<PbtNodeRecord> records = [];
                for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
                {
                    if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
                    PbtStorageNodePath path = PbtFourLevelGroupGeometry.PathOf(storageGroupKey, position);
                    byte[]? encoding = overrideNode(path);
                    if (encoding is not null) records.Add(new PbtNodeRecord(path, encoding));
                }

                if (records.Count == 0) return null;
                BufferWriter writer = new(MemoryProvider);
                try
                {
                    PbtNodeGroupEncoder.Encode(ref writer, storageGroupKey, records, default);
                    RefCountingMemory payload = writer.Detach()!;
                    return payload;
                }
                catch
                {
                    writer.Dispose();
                    throw;
                }
            }
            RefCountingMemory? innerPayload = Inner.GetNodeGroup(groupKey, hash);
            return innerPayload;
        }

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 hash, RefCountingMemory? payload)
        {
            Applies++;
            OnApply?.Invoke(groupKey.ToPath<PbtStorageNodePath>());
            if (ThrowOnApply) throw new InvalidOperationException("Configured write failure.");
            Inner.SetNodeGroup(groupKey, hash, payload);
        }

        internal void ResetReads()
        {
            Reads = 0;
            GroupReads.Clear();
        }
    }


    private static byte[] Hash(byte[] preimage)
    {
        byte[] result = new byte[32];
        global::Blake3.Hasher.Hash(preimage, result);
        return result;
    }

    /// <summary>An account-zone key whose bytes after the zone byte start with <paramref name="suffix"/>.</summary>
    private static byte[] AccountKey(params ReadOnlySpan<byte> suffix)
    {
        byte[] key = new byte[PbtPath.KeyLength];
        suffix.CopyTo(key.AsSpan(1));
        return key;
    }

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }
}
