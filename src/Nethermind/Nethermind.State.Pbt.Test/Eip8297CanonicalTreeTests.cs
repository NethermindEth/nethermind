// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class Eip8297CanonicalTreeTests
{
    [Test]
    public void Small_and_wide_generic_folds_match_oracle_for_identical_34_byte_inputs(
        [Values(false, true)] bool prepared,
        [Values(false, true)] bool zeroDeletes)
    {
        using PbtNodeGroupStore smallStore = new();
        using PbtNodeGroupStore wideStore = new();
        EipReferenceTree oracle = new();
        ValueHash256 smallRoot = default;
        ValueHash256 wideRoot = default;
        for (int round = -1; round < 3; round++)
        {
            using PbtWriteBatchBuilder<PbtFullKey> small = new(0);
            using PbtWriteBatchBuilder<PbtStorageFullKey> wide = new(0);
            for (int index = 0; index < 64; index++)
            {
                byte[] key = new byte[34];
                key[0] = (byte)(index % 2);
                key[1] = (byte)(index / 4);
                key[^1] = (byte)index;
                byte[]? value = round == -1 || round == 2 || (round == 1 && index % 3 == 0) ? null : Value((byte)(index + round + 1));
                ValueHash256? leaf = value is null ? null : new ValueHash256(value);
                small.Set(new PbtFullKey(key), new ValueHash256(Value(1)));
                wide.Set(new PbtStorageFullKey(key), new ValueHash256(Value(1)));
                if (zeroDeletes)
                {
                    small.Set(new PbtFullKey(key), default);
                    wide.Set(new PbtStorageFullKey(key), default);
                }
                else
                {
                    small.Delete(new PbtFullKey(key));
                    wide.Delete(new PbtStorageFullKey(key));
                }
                if (leaf is { } nonzeroValue)
                {
                    small.Set(new PbtFullKey(key), nonzeroValue);
                    wide.Set(new PbtStorageFullKey(key), nonzeroValue);
                }
                if (value is null) oracle.Delete(key);
                else oracle.Insert(key, value);
            }
            smallRoot = prepared
                ? TrieUpdater<PbtFullKey, PbtNodePath>.UpdateRoot(smallStore, smallRoot, PbtWriteBatchSet<PbtFullKey>.Create(small.Build()))
                : TrieUpdater<PbtFullKey, PbtNodePath>.UpdateRoot(smallStore, smallRoot, small.Build());
            wideRoot = prepared
                ? TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.UpdateRoot(wideStore, wideRoot, PbtWriteBatchSet<PbtStorageFullKey>.Create(wide.Build()))
                : TrieUpdater<PbtStorageFullKey, PbtStorageNodePath>.UpdateRoot(wideStore, wideRoot, wide.Build());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(smallRoot, Is.EqualTo(wideRoot), $"round {round}");
                Assert.That(smallRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"round {round}");
                IReadOnlyList<PbtNodeRecord> smallRecords = smallStore.EnumerateRecords();
                IReadOnlyList<PbtNodeRecord> wideRecords = wideStore.EnumerateRecords();
                Assert.That(smallRecords.Count, Is.EqualTo(wideRecords.Count), $"round {round}");
                for (int index = 0; index < Math.Min(smallRecords.Count, wideRecords.Count); index++)
                {
                    Assert.That(smallRecords[index].Path.Encode(), Is.EqualTo(wideRecords[index].Path.Encode()));
                    Assert.That(smallRecords[index].Encoding.ToArray(), Is.EqualTo(wideRecords[index].Encoding.ToArray()));
                }
            }
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(131)]
    public void Builder_prepares_selected_nibble_and_independent_single_use_outputs(int shardNibbleIndex)
    {
        using PbtWriteBatchBuilder<PbtStorageFullKey> builder = new(shardNibbleIndex);
        PbtStorageFullKey Key(byte nibble, byte suffix)
        {
            byte[] bytes = new byte[Math.Max(2, shardNibbleIndex / 2 + 1)];
            bytes[0] = suffix;
            bytes[shardNibbleIndex / 2] = (byte)((shardNibbleIndex & 1) == 0 ? nibble << 4 : nibble);
            if (shardNibbleIndex < 2) bytes[1] = suffix;
            return new(bytes);
        }
        PbtStorageFullKey setKey = Key(15, 1);
        PbtStorageFullKey deleteKey = Key(2, 2);
        PbtStorageFullKey zeroKey = Key(8, 3);
        builder.Set(setKey, new ValueHash256(Value(1)));
        builder.Delete(setKey);
        builder.Set(setKey, new ValueHash256(Value(2)));
        builder.Set(deleteKey, new ValueHash256(Value(1)));
        builder.SetLeaf(deleteKey, default(ValueHash256));
        builder.Set(zeroKey, default);
        using PbtWriteBatch<PbtStorageFullKey> first = builder.Build();
        using PbtWriteBatch<PbtStorageFullKey> second = builder.Build();
        TrieUpdater.BucketPlan plan = first.Plan;
        byte[] buffer = new byte[plan.GetBufferSize(first.Count)];
        TrieUpdater.PartitionOutcome outcome = plan.WithBuffer(buffer).BucketSort(first.Entries.ToArray().AsSpan(), null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Precalculated.Length, Is.EqualTo(17));
            Assert.That(plan.Depth, Is.EqualTo(shardNibbleIndex * 4));
            Assert.That(plan.BranchDepth, Is.EqualTo(plan.Depth));
            Assert.That(plan.IsSorted, Is.False);
            Assert.That(plan.PrefixesValidated, Is.False);
            Assert.That(plan.Precalculated[0], Is.EqualTo((1 << 2) | (1 << 8) | (1 << 15)));
            Assert.That(plan.Precalculated.Slice(1, 3).ToArray(), Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(buffer, Is.Empty);
            Assert.That(outcome.UsedMask, Is.EqualTo(plan.Precalculated[0]));
            Assert.That(outcome.Counts.ToArray(), Is.EqualTo(new[] { 1, 1, 1 }));
        }
        first.Consume(out ArrayPoolList<PbtWriteOperation<PbtStorageFullKey>> operations, out ArrayPoolList<int> table);
        using ArrayPoolList<PbtWriteOperation<PbtStorageFullKey>> ownedOperations = operations;
        using ArrayPoolList<int> ownedTable = table;
        first.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.ShardNibbleIndex, Is.EqualTo(shardNibbleIndex));
            Assert.That(builder.Count, Is.EqualTo(3));
            Assert.That(builder.Leaves, Does.Contain(new KeyValuePair<PbtStorageFullKey, ValueHash256?>(zeroKey, null)));
            Assert.That(table[0], Is.EqualTo((1 << 2) | (1 << 8) | (1 << 15)));
            Assert.That(table.AsSpan().Slice(1, 3).ToArray(), Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(operations, Is.EqualTo(new[]
            {
                PbtWriteOperation<PbtStorageFullKey>.Delete(deleteKey),
                PbtWriteOperation<PbtStorageFullKey>.Set(zeroKey, default),
                PbtWriteOperation<PbtStorageFullKey>.Set(setKey, new ValueHash256(Value(2))),
            }));
            Assert.Throws<InvalidOperationException>(() => first.Consume(out _, out _));
            Assert.Throws<InvalidOperationException>(() => _ = first.Count);
            Assert.Throws<InvalidOperationException>(() => { _ = first.Plan; });
        }
        PbtWriteOperation<PbtStorageFullKey>[] expected = operations.AsSpan().ToArray();
        operations.AsSpan().Clear();
        table.AsSpan().Clear();
        builder.Reset();
        second.Consume(out ArrayPoolList<PbtWriteOperation<PbtStorageFullKey>> independentOperations, out ArrayPoolList<int> independentTable);
        using ArrayPoolList<PbtWriteOperation<PbtStorageFullKey>> ownedIndependentOperations = independentOperations;
        using ArrayPoolList<int> ownedIndependentTable = independentTable;
        using PbtWriteBatch<PbtStorageFullKey> empty = builder.Build();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(independentOperations, Is.EqualTo(expected));
            Assert.That(independentTable[0], Is.EqualTo((1 << 2) | (1 << 8) | (1 << 15)));
            Assert.That(empty.Plan.Precalculated.Length, Is.EqualTo(17));
            Assert.That(empty.Count, Is.Zero);
            Assert.That(empty.ShardNibbleIndex, Is.EqualTo(shardNibbleIndex));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Disposed_prepared_batches_release_owned_lists(bool grouped)
    {
        using ArrayPoolList<PbtWriteOperation<PbtStorageFullKey>> operations = new(1, 1);
        using ArrayPoolList<int> table = new(17, 17);
        using PbtWriteBatch<PbtStorageFullKey> batch = new(operations, table, 0);
        using PbtWriteBatchSet<PbtStorageFullKey>? prepared = grouped ? PbtWriteBatchSet<PbtStorageFullKey>.Create(batch) : null;

        if (prepared is not null) prepared.Dispose();
        else batch.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ObjectDisposedException>(() => operations.AsSpan());
            Assert.Throws<ObjectDisposedException>(() => table.AsSpan());
            Assert.Throws<InvalidOperationException>(() => batch.Consume(out _, out _));
            if (prepared is not null) Assert.Throws<InvalidOperationException>(() => prepared.Consume(out _, out _));
        }
    }

    [TestCase(0, false)]
    [TestCase(0, true)]
    [TestCase(1, false)]
    [TestCase(1, true)]
    [TestCase(2, false)]
    [TestCase(2, true)]
    public void Updater_releases_consumed_lists_on_success_and_failure(int mode, bool fail)
    {
        CountingPbtStore store = new() { ThrowOnApply = fail };
        using ArrayPoolList<PbtWriteOperation<PbtFullKey>> operations = new(1);
        operations.Add(PbtWriteOperation<PbtFullKey>.Set(new PbtFullKey(Bytes.FromHexString("0000")), new ValueHash256(Value(1))));
        using ArrayPoolList<int> table = new(17, 17);
        table[0] = 1;
        table[1] = 1;
        using PbtWriteBatch<PbtFullKey> batch = new(operations, table, mode == 2 ? 2 : 0);
        using PbtWriteBatchSet<PbtFullKey>? prepared = mode == 1 ? PbtWriteBatchSet<PbtFullKey>.Create(batch) : null;
        Action update = () =>
        {
            if (mode == 2) TrieUpdater.UpdateRoot(store, default, new PbtPartitionBatches { Account = batch });
            else if (prepared is not null) TrieUpdater.UpdateRoot(store, default, prepared);
            else TrieUpdater.UpdateRoot(store, default, batch);
        };

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
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtWriteBatchBuilder<PbtStorageFullKey>(shardNibbleIndex));

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(131)]
    public void Builder_rejects_keys_missing_selected_nibble_before_mutation(int shardNibbleIndex)
    {
        using PbtWriteBatchBuilder<PbtStorageFullKey> builder = new(shardNibbleIndex);
        PbtStorageFullKey shortKey = shardNibbleIndex < 2 ? default : new(new byte[shardNibbleIndex / 2]);
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentException>(() => builder.Set(shortKey, default));
            Assert.Throws<ArgumentException>(() => builder.Delete(shortKey));
            Assert.Throws<ArgumentException>(() => builder.SetLeaf(default, null));
            Assert.That(builder.Count, Is.Zero);
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void Builder_generic_fold_preserves_prefix_replacement_and_reopened_root(int shardNibbleIndex)
    {
        using PbtWriteBatchBuilder<PbtStorageFullKey> builder = new(shardNibbleIndex);
        byte[] originalKey = Bytes.FromHexString("1234AB");
        byte[] replacementKey = Bytes.FromHexString("1234");
        byte[] otherKey = Bytes.FromHexString("F012");
        using PbtNodeGroupStore store = new();
        builder.Set(new(originalKey), new ValueHash256(Value(1)));
        builder.Set(new(otherKey), new ValueHash256(Value(2)));
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, builder.Build());
        builder.Reset();
        builder.Set(new(replacementKey), new ValueHash256(Value(3)));
        builder.Delete(new(originalKey));
        root = TrieUpdater.UpdateRoot(store, root, builder.Build());
        EipReferenceTree oracle = new();
        oracle.Insert(replacementKey, Value(3));
        oracle.Insert(otherKey, Value(2));
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.ExportPhysicalPayloads());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(TrieUpdater.UpdateRoot(reopened, root, builder.Build()), Is.EqualTo(root));
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
    public void Path_append_and_prefix_composition_match_bit_reference(
        [Range(0, 7)] int pathOffset,
        [Values(0, 1, 7, 8, 9, 63, 64, 65, 511)] int prefixLength,
        [Values(0, 1)] int direction)
    {
        byte[] keyBytes = new byte[PbtStorageFullKey.MaxLength];
        new Random(8297).NextBytes(keyBytes);
        PbtStorageFullKey key = new(keyBytes);
        int pathDepth = 8 + pathOffset;
        PbtStorageNodePath path = PbtStorageNodePath.FromKey(key, pathDepth);
        PbtBitPrefix prefix = PbtBitPrefix.FromKey(key, pathOffset, prefixLength);
        byte[] expectedPrefix = new byte[(prefixLength + 7) >> 3];
        CopyBitsReference(keyBytes, pathOffset, prefixLength, expectedPrefix, 0);
        int resultDepth = pathDepth + prefixLength + 1;
        byte[] expectedPath = new byte[(resultDepth + 7) >> 3];
        CopyBitsReference(keyBytes, 0, pathDepth, expectedPath, 0);
        CopyBitsReference(expectedPrefix, 0, prefixLength, expectedPath, pathDepth);
        expectedPath[(resultDepth - 1) >> 3] |= (byte)(direction << (7 - ((resultDepth - 1) & 7)));

        PbtStorageNodePath appended = path.Append(prefix, direction);
        PbtBitPrefix concatenated = PbtBitPrefix.Concat(new PbtBitPrefix(path.Path, pathDepth), direction, prefix);
        byte[] expectedConcat = new byte[expectedPath.Length];
        CopyBitsReference(keyBytes, 0, pathDepth, expectedConcat, 0);
        expectedConcat[pathDepth >> 3] |= (byte)(direction << (7 - (pathDepth & 7)));
        CopyBitsReference(expectedPrefix, 0, prefixLength, expectedConcat, pathDepth + 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(prefix.Bytes.ToArray(), Is.EqualTo(expectedPrefix));
            Assert.That(appended, Is.EqualTo(new PbtStorageNodePath(expectedPath, resultDepth)));
            Assert.That(concatenated, Is.EqualTo(new PbtBitPrefix(expectedConcat, resultDepth)));
        }
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
        oracle.Delete(keys[1]);
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
        byte[] leftKey = new byte[PbtStorageFullKey.MaxLength];
        byte[] rightKey = new byte[PbtStorageFullKey.MaxLength];
        rightKey[^1] = 1;
        (byte[] Key, byte[]? Value)[] changes = [(leftKey, Value(1)), (rightKey, Value(2))];
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();

        tree.ApplyBatch(changes);
        ApplyOracle(oracle, changes);

        Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
    }

    [TestCase(1)]
    [TestCase(32)]
    [TestCase(PbtStorageFullKey.MaxLength)]
    public void Compressed_boundary_cursor_survives_replacement_collapse_and_reinsertion(int keyLength)
    {
        byte[] leftKey = new byte[keyLength];
        byte[] rightKey = new byte[keyLength];
        rightKey[^1] = 1;
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
            Assert.That(store.Reads, Is.GreaterThanOrEqualTo(1), "the authoritative root group is inspected before the conflict is reported");
            Assert.That(store.Applies, Is.Zero);
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
        store.ResetReads();

        Assert.Throws<ArgumentException>(() => TrieUpdater.UpdateRoot(store, root, Batch(
            ([0x12], null), ([0x34, 0x56], Value(2)), ([0x80], Value(3)), ([0x34], Value(4)))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Reads, Is.GreaterThan(0), "prefix conflicts are detected during traversal");
        }

        AssertAllMemoryReleased(store);
    }

    [Test]
    public void Persisted_prefix_conflict_after_a_sibling_was_staged_is_atomic()
    {
        CountingPbtStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(([0x10], Value(1)), ([0x80], Value(2))));
        store.ResetReads();

        Assert.Throws<ArgumentException>(() => TrieUpdater.UpdateRoot(store, root, Batch(
            ([0x20], Value(3)), ([0x80, 0x01], Value(4)))));

        AssertAllMemoryReleased(store);
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
        byte[] source = new byte[PbtStorageFullKey.MaxLength];
        source.AsSpan().Fill(0xA5);
        PbtStorageFullKey key = new(source);
        PbtStorageNodePath fromKey = PbtStorageNodePath.FromKey(key, bitDepth);
        byte[] expected = source.AsSpan(0, (bitDepth + 7) >> 3).ToArray();
        if ((bitDepth & 7) != 0) expected[^1] &= (byte)(0xFF << (8 - (bitDepth & 7)));
        byte[] constructorInput = (byte[])expected.Clone();
        PbtStorageNodePath constructed = new(constructorInput, bitDepth);
        constructorInput.AsSpan().Clear();
        source.AsSpan().Clear();
        PbtStorageNodePath decoded = PbtStorageNodePath.Decode(constructed.Encode());
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(constructed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(constructed.Path.ToArray(), Is.EqualTo(expected));
            Assert.That(constructed.Encode().Length, Is.EqualTo(4 + expected.Length));
            Assert.That(fromKey, Is.EqualTo(constructed));
            Assert.That(decoded, Is.EqualTo(constructed));
            Assert.That(decoded.GetHashCode(), Is.EqualTo(constructed.GetHashCode()));
            Assert.That(decoded.CompareTo(constructed), Is.Zero);
            Assert.That(PbtFourLevelGroupGeometry.PathOf(location.GroupKey, location.Position), Is.EqualTo(constructed));
            if (bitDepth > 0)
            {
                PbtStorageNodePath parent = PbtStorageNodePath.FromKey(key, bitDepth - 1);
                Assert.That(parent.Append(new PbtBitPrefix([], 0), key.GetBit(bitDepth - 1)), Is.EqualTo(constructed));
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
        PbtStorageFullKey key = new(source);
        PbtStorageFullKey copy = key;
        source[^1] ^= 1;
        PbtStorageFullKey different = new(source);
        source[^1] ^= 1;
        PbtStorageFullKey equal = new(source);
        Dictionary<PbtStorageFullKey, int> keys = new() { [key] = 42 };
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
            Assert.That(key.GetHashCode(), Is.EqualTo(default(PbtStorageFullKey).GetHashCode()));
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
            Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageFullKey(new byte[length]));
            Assert.Throws<InvalidDataException>(() => new PbtNodeReader(encoding));
        }
    }

    [Test]
    public void Invalid_inline_paths_and_default_complete_keys_reject_before_mutation()
    {
        PbtStorageNodePath maximum = new(new byte[66], 528);
        using PbtWriteBatchBuilder<PbtStorageFullKey> batch = new(0);
        using PbtWriteBatchBuilder<PbtStorageFullKey> builder = new(2);
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageNodePath(new byte[67], 529));
            Assert.Throws<InvalidDataException>(() => PbtStorageNodePath.Decode([0, 0, 2, 17, .. new byte[67]]));
            Assert.Throws<ArgumentOutOfRangeException>(() => maximum.Append(new PbtBitPrefix([], 0), 0));
            Assert.Throws<ArgumentException>(() => new PbtStorageNodePath(Bytes.FromHexString("01"), 1));
            Assert.Throws<ArgumentException>(() => new PbtStorageNodePath([], 8));
            Assert.Throws<ArgumentException>(() => batch.Set(default, default));
            Assert.Throws<ArgumentException>(() => batch.Delete(default));
            Assert.Throws<ArgumentException>(() => builder.SetLeaf(default, default));
            Assert.Throws<ArgumentException>(() => PbtNodeCodec.EncodeLeaf(default(PbtStorageFullKey), new byte[32]));
            Assert.That(batch.Count, Is.Zero);
            Assert.That(builder.Leaves, Is.Empty);
        }
    }

    [Test]
    public void Full_key_and_persisted_path_validate_bounds_and_canonical_padding()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageFullKey([]));
        Assert.DoesNotThrow(() => new PbtStorageFullKey(new byte[PbtStorageFullKey.MaxLength]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtStorageFullKey(new byte[PbtStorageFullKey.MaxLength + 1]));
        Assert.Throws<ArgumentException>(() => new PbtBitPrefix([0x01], 1));
        Assert.Throws<InvalidDataException>(() => PbtStorageNodePath.Decode([0, 0, 0, 1, 0x01]));

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

    [TestCase(1, 188)]
    [TestCase(34, 189)]
    [TestCase(66, 190)]
    public void Node_hashes_match_independent_preimages_at_stack_threshold(int keyLength, int prefixLength)
    {
        byte[] key = new byte[keyLength];
        key[^1] = 1;
        byte[] value = Value(7);
        PbtNodeReader leaf = new(PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(key), value));

        int prefixBitCount = prefixLength * 8;
        byte[] prefixBytes = new byte[prefixLength];
        ValueHash256 left = new(Hash([0, .. key, .. value]));
        ValueHash256 right = new(Value(8));
        PbtNodeReader branch = new(PbtNodeCodec.EncodeBranch(prefixBytes, prefixBitCount, left, right));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PbtNodeCodec.Hash(leaf).Bytes.ToArray(), Is.EqualTo(Hash([0, .. key, .. value])), "leaf");
            Assert.That(PbtNodeCodec.Hash(branch).Bytes.ToArray(), Is.EqualTo(Hash(
                [1, (byte)(prefixBitCount >> 8), (byte)prefixBitCount, .. prefixBytes, .. left.Bytes, .. right.Bytes])), "branch");
        }
    }

    [TestCase(true, 1)]
    [TestCase(true, 66)]
    [TestCase(false, 0)]
    [TestCase(false, 5)]
    [TestCase(false, 8)]
    [TestCase(false, 257)]
    [TestCase(false, ushort.MaxValue)]
    public void Node_reader_borrows_fields_and_preserves_encoding_and_hash(bool leaf, int length)
    {
        byte[] field = new byte[leaf ? length : (length + 7) / 8];
        field.AsSpan().Fill(0xA0);
        if (!leaf && length % 8 != 0) field[^1] &= (byte)(0xFF << (8 - length % 8));
        byte[] value = Value(7);
        ValueHash256 left = new(Value(8));
        ValueHash256 right = new(Value(9));
        byte[] expected = leaf
            ? [0, (byte)(length >> 8), (byte)length, .. field, .. value]
            : [1, (byte)(length >> 8), (byte)length, .. field, .. left.Bytes, .. right.Bytes];
        byte[] encoding = leaf
            ? PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(field), value)
            : PbtNodeCodec.EncodeBranch(field, length, left, right);
        byte[] backing = new byte[encoding.Length + 11];
        encoding.CopyTo(backing, 7);
        PbtNodeReader reader = new(backing.AsSpan(7, encoding.Length));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.IsLeaf, Is.EqualTo(leaf));
            Assert.That(reader.Encoding.ToArray(), Is.EqualTo(expected));
            Assert.That(reader.Encoding.Overlaps(backing.AsSpan(), out int offset), Is.True);
            Assert.That(offset, Is.EqualTo(-7));
            Assert.That(PbtNodeCodec.Hash(reader).Bytes.ToArray(), Is.EqualTo(Hash(leaf ? [0, .. field, .. value] : expected)));
            if (leaf)
            {
                Assert.That(reader.Key.ToArray(), Is.EqualTo(field));
                Assert.That(reader.Value.ToArray(), Is.EqualTo(value));
                Assert.That(reader.Key.Overlaps(backing.AsSpan()), Is.True);
                Assert.That(reader.Value.Overlaps(backing.AsSpan()), Is.True);
            }
            else
            {
                Assert.That(reader.PrefixBitCount, Is.EqualTo(length));
                Assert.That(reader.Prefix.ToArray(), Is.EqualTo(field));
                if (length != 0) Assert.That(reader.Prefix.Overlaps(backing.AsSpan()), Is.True);
                Assert.That(reader.LeftHash, Is.EqualTo(left));
                Assert.That(reader.RightHash, Is.EqualTo(right));
            }
        }
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
            byte[] invalidLeaf = new byte[3 + length + 32];
            invalidLeaf[2] = (byte)length;
            yield return new TestCaseData(invalidLeaf).SetName($"Node_reader_rejects_invalid_leaf_length_{length}");
        }
        byte[] leaf = PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(Bytes.FromHexString("A0")), Value(1));
        yield return new TestCaseData(leaf[..^1]).SetName("Node_reader_rejects_truncated_leaf");
        yield return new TestCaseData((byte[])[.. leaf, 0]).SetName("Node_reader_rejects_trailing_leaf_bytes");
        byte[] branch = PbtNodeCodec.EncodeBranch(Bytes.FromHexString("A0"), 5, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
        yield return new TestCaseData(branch[..^1]).SetName("Node_reader_rejects_truncated_branch");
        yield return new TestCaseData((byte[])[.. branch, 0]).SetName("Node_reader_rejects_trailing_branch_bytes");
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

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void Node_reader_rejects_default_and_wrong_kind_access(int kind)
    {
        byte[] encoding = kind == 1
            ? PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(Bytes.FromHexString("A0")), Value(1))
            : PbtNodeCodec.EncodeBranch([], 0, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
        Assert.Throws<InvalidOperationException>(() =>
        {
            PbtNodeReader reader = kind == 0 ? default : new(encoding);
            if (kind == 0) _ = reader.Encoding.Length;
            else if (kind == 1) _ = reader.PrefixBitCount;
            else _ = reader.Key.Length;
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Node_reader_construction_and_field_access_do_not_allocate(bool leaf)
    {
        byte[] encoding = leaf
            ? PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(Bytes.FromHexString("A0")), Value(1))
            : PbtNodeCodec.EncodeBranch(Bytes.FromHexString("A0"), 5, new ValueHash256(Value(1)), new ValueHash256(Value(2)));
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
            ? reader.Key[0] + reader.Value[0]
            : reader.PrefixBitCount + reader.Prefix[0] + reader.LeftHash.Bytes[0] + reader.RightHash.Bytes[0]);
    }

    [Test]
    public void Current_key_derivation_emits_exact_zone_lengths()
    {
        byte[] address32 = new byte[32];
        address32[0] = 0xA5;
        PbtFullKey account = Eip8297KeyDerivation.AccountKey(address32, 0);
        PbtStorageFullKey headerStorage = Eip8297KeyDerivation.StorageKey(address32, new Nethermind.Int256.UInt256(63));
        PbtStorageFullKey overflowStorage = Eip8297KeyDerivation.StorageKey(address32, new Nethermind.Int256.UInt256(64));
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
    public void Bucket_plan_uses_exact_scratch_size(
        [Values(0, 1, 3, 16, 17, 33)] int count,
        [Values(0, 3, 4, 8)] int branchDepth,
        [Values(false, true)] bool precomputed,
        [Values(false, true)] bool sorted)
    {
        PbtWriteOperation<PbtStorageFullKey>[] operations = new PbtWriteOperation<PbtStorageFullKey>[count];
        for (int index = 0; index < count; index++)
        {
            byte[] key = Bytes.FromHexString("0xAA0000");
            key[^1] = (byte)(sorted ? index : count - index - 1);
            operations[index] = PbtWriteOperation<PbtStorageFullKey>.Set(new PbtStorageFullKey(key), new ValueHash256(Value(1)));
        }
        PbtWriteOperation<PbtStorageFullKey>[] original = (PbtWriteOperation<PbtStorageFullKey>[])operations.Clone();
        int[] table = new int[17];
        table[0] = count == 0 ? 0 : 1 << 10;
        table[1] = count;
        TrieUpdater.BucketPlan plan = new(precomputed ? table : default, 0, branchDepth, sorted, false);
        int expectedSize = precomputed || count == 0 ? 0 : sizeof(int) * (branchDepth >= 4 ? 1 : Math.Min(count, 16));
        byte[] buffer = new byte[plan.GetBufferSize(count)];
        Array.Fill(buffer, (byte)0xFF);
        TrieUpdaterMetrics metrics = new();

        plan = plan.WithBuffer(buffer);
        TrieUpdater.PartitionOutcome outcome = plan.BucketSort(operations, metrics);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffer.Length, Is.EqualTo(expectedSize));
            Assert.That(outcome.UsedMask, Is.EqualTo(table[0]));
            Assert.That(outcome.Counts.ToArray(), Is.EqualTo(count == 0 ? Array.Empty<int>() : new[] { count }));
            Assert.That(operations, Is.EquivalentTo(original));
            Assert.That(outcome.Plan.Depth, Is.Zero);
            Assert.That(metrics.PrecalculatedLevels, Is.EqualTo(precomputed ? 1 : 0));
            if (precomputed) Assert.That(operations, Is.EqualTo(original));
        }
    }

    private static IEnumerable<TestCaseData> BucketizationCases()
    {
        foreach (int count in new[] { 0, 1, 2, 3, 4, 16, 31, 32, 33, 256 })
        foreach (int groupDepth in new[] { 0, 4, 20 })
        foreach (int occupiedSlots in new[] { 1, 2, 16 })
        foreach (int order in new[] { 0, 1, 2 })
            yield return new TestCaseData(count, groupDepth, occupiedSlots, order);
    }

    [TestCaseSource(nameof(BucketizationCases))]
    public void Boundary_bucketization_preserves_operations_and_orders_destinations(int count, int groupDepth, int occupiedSlots, int order)
    {
        List<(byte[] Key, byte[]? Value)> changes = BoundaryChanges(count, groupDepth, occupiedSlots, order);
        PbtWriteOperation<PbtStorageFullKey>[] operations = new PbtWriteOperation<PbtStorageFullKey>[count];
        for (int index = 0; index < count; index++)
        {
            PbtStorageFullKey key = new(changes[index].Key);
            operations[index] = index % 3 == 0
                ? PbtWriteOperation<PbtStorageFullKey>.Delete(key)
                : PbtWriteOperation<PbtStorageFullKey>.Set(key, new ValueHash256(changes[index].Value!));
        }
        PbtWriteOperation<PbtStorageFullKey>[] original = (PbtWriteOperation<PbtStorageFullKey>[])operations.Clone();

        TrieUpdater.BucketPlan plan = new(default, groupDepth, 0, false, false);
        byte[] buffer = new byte[plan.GetBufferSize(count)];
        Array.Fill(buffer, (byte)0xFF);
        TrieUpdater.PartitionOutcome partition = plan.WithBuffer(buffer).BucketSort(operations, null);
        int expectedMask = 0;
        int expectedBranchDepth = count == 0 ? groupDepth : original[0].Key.BitLength;
        for (int index = 0; index < count; index++)
        {
            PbtStorageFullKey key = original[index].Key;
            expectedMask |= 1 << ((key.Bytes[groupDepth / 8] >> (4 - groupDepth % 8)) & 15);
            int bit = groupDepth;
            int end = Math.Min(original[0].Key.BitLength, key.BitLength);
            while (bit < end && original[0].Key.GetBit(bit) == key.GetBit(bit)) bit++;
            expectedBranchDepth = Math.Min(expectedBranchDepth, bit);
        }

        int[] destinations = new int[count];
        int[] bucketCounts = new int[PbtFourLevelGroupGeometry.BoundarySlots];
        for (int index = 0; index < count; index++)
        {
            destinations[index] = (operations[index].Key.Bytes[groupDepth / 8] >> (4 - groupDepth % 8)) & 15;
            bucketCounts[destinations[index]]++;
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(partition.UsedMask, Is.EqualTo(expectedMask));
            Assert.That(partition.Plan.BranchDepth, occupiedSlots == 1
                ? Is.EqualTo(expectedBranchDepth)
                : Is.InRange(groupDepth, expectedBranchDepth));
            Assert.That(destinations, Is.Ordered);
            Assert.That(operations, Is.EquivalentTo(original));
            List<int> expectedCounts = [];
            foreach (int bucketCount in bucketCounts)
                if (bucketCount != 0) expectedCounts.Add(bucketCount);
            Assert.That(partition.Counts.ToArray(), Is.EqualTo(expectedCounts));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Prepared_partition_tables_match_every_range_and_preserve_public_batch(bool fallback)
    {
        using PbtWriteBatchBuilder<PbtStorageFullKey> batch = new(0);
        foreach (byte zone in new byte[] { 0xFF, 0x01, 0x00 })
        foreach (byte shard in new byte[] { 0xFF, 0x00, 0x31, 0x3F })
        {
            byte[] key = new byte[zone == 0xFF ? 66 : 34];
            key[0] = zone;
            key[1] = shard;
            PbtStorageFullKey fullKey = new(key);
            batch.Set(fullKey, new ValueHash256(Value(1)));
            batch.Delete(fullKey);
            batch.Set(fullKey, new ValueHash256(Value(2)));
            key[^1] = 1;
            batch.Delete(new PbtStorageFullKey(key));
        }
        if (fallback) batch.Set(new PbtStorageFullKey(Bytes.FromHexString("0x42")), new ValueHash256(Value(3)));
        using PbtWriteBatchSet<PbtStorageFullKey> prepared = PbtWriteBatchSet<PbtStorageFullKey>.Create(batch.Build());
        PbtWriteOperation<PbtStorageFullKey>[] original = [.. batch.Operations];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(prepared.Count, Is.EqualTo(batch.Count));
            Assert.That(prepared.Entries.ToArray(), Is.EquivalentTo(original));
            Assert.That(prepared.Precalculated.IsEmpty, Is.EqualTo(fallback));
            foreach (PbtPartition partition in new[] { PbtPartition.Account, PbtPartition.Code, PbtPartition.Storage })
            {
                ReadOnlySpan<PbtWriteOperation<PbtStorageFullKey>> entries = prepared[partition];
                Assert.That(entries.Length, Is.EqualTo(fallback ? 0 : 8));
                byte zone = partition == PbtPartition.Storage ? (byte)0xFF : (byte)partition;
                foreach (PbtWriteOperation<PbtStorageFullKey> entry in entries) Assert.That(entry.Key.Bytes[0], Is.EqualTo(zone));
            }
        }
        if (!fallback) AssertPreparedLevel(prepared.Entries, prepared.Precalculated, 0);
        prepared.Consume(out ArrayPoolList<PbtWriteOperation<PbtStorageFullKey>> operations, out ArrayPoolList<int> table);
        using ArrayPoolList<PbtWriteOperation<PbtStorageFullKey>> ownedOperations = operations;
        using ArrayPoolList<int> ownedTable = table;
        prepared.Dispose();
        operations.Reverse();
        using PbtWriteBatchSet<PbtStorageFullKey> independent = PbtWriteBatchSet<PbtStorageFullKey>.Create(batch.Build());
        Assert.Throws<InvalidOperationException>(() => prepared.Consume(out _, out _));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.Operations, Is.EqualTo(original));
            Assert.That(independent.Entries.ToArray(), Is.EquivalentTo(original));
            Assert.That(table.Count, Is.EqualTo(fallback ? 0 : 17));
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void Prepared_partition_fold_matches_generic_and_consumes_producer_levels(bool persisted, bool accumulated)
    {
        using PbtWriteBatchBuilder<PbtStorageFullKey> batch = new(0);
        EipReferenceTree oracle = new();
        foreach (byte zone in new byte[] { 0x00, 0x01, 0xFF })
        foreach (byte shard in new byte[] { 0x00, 0x01, 0xF0, 0xFF })
        {
            byte[] key = new byte[zone == 0xFF ? 66 : 34];
            key[0] = zone;
            key[1] = shard;
            batch.Set(new PbtStorageFullKey(key), new ValueHash256(Value(1)));
            oracle.Insert(key, Value(1));
        }
        using PbtNodeGroupStore preparedStore = new();
        using PbtNodeGroupStore genericStore = new();
        ValueHash256 initialRoot = persisted ? TrieUpdater.UpdateRoot(preparedStore, default, batch.Build()) : default;
        if (persisted) TrieUpdater.UpdateRoot(genericStore, default, batch.Build());
        TrieUpdaterMetrics metrics = new();
        ValueHash256 preparedRoot;
        if (accumulated)
        {
            using PbtWriteBatchBuilder<PbtFullKey> account = new(2);
            using PbtWriteBatchBuilder<PbtFullKey> code = new(2);
            using PbtWriteBatchBuilder<PbtStorageFullKey> storage = new(2);
            foreach (PbtWriteOperation<PbtStorageFullKey> operation in batch.Operations)
            {
                switch (operation.Key.Bytes[0])
                {
                    case 0x00:
                        account.SetLeaf((PbtFullKey)operation.Key, null);
                        account.SetLeaf((PbtFullKey)operation.Key, operation.Value);
                        break;
                    case 0x01:
                        code.SetLeaf((PbtFullKey)operation.Key, null);
                        code.SetLeaf((PbtFullKey)operation.Key, operation.Value);
                        break;
                    case 0xFF:
                        storage.SetLeaf(operation.Key, null);
                        storage.SetLeaf(operation.Key, operation.Value);
                        break;
                }
            }
            using PbtPartitionBatches prepared = new() { Account = account.Build(), Code = code.Build(), Storage = storage.Build() };
            AssertPreparedLevel(prepared.Account.Entries, prepared.Account.Plan.Precalculated, 8);
            AssertPreparedLevel(prepared.Code.Entries, prepared.Code.Plan.Precalculated, 8);
            AssertPreparedLevel(prepared.Storage.Entries, prepared.Storage.Plan.Precalculated, 8);
            preparedRoot = TrieUpdater.UpdateRoot(preparedStore, initialRoot, prepared, metrics);
        }
        else
        {
            using PbtWriteBatchSet<PbtStorageFullKey> prepared = PbtWriteBatchSet<PbtStorageFullKey>.Create(batch.Build());
            AssertPreparedLevel(prepared.Entries, prepared.Precalculated, 0);
            preparedRoot = TrieUpdater.UpdateRoot(preparedStore, initialRoot, prepared, metrics);
        }
        ValueHash256 genericRoot = TrieUpdater.UpdateRoot(genericStore, initialRoot, batch.Build());
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(preparedStore.ExportPhysicalPayloads());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preparedRoot, Is.EqualTo(genericRoot));
            Assert.That(preparedRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(metrics.PrecalculatedLevels, Is.EqualTo(accumulated ? 3 : 1));
            Assert.That(metrics.FullKeySorts, Is.EqualTo(accumulated ? 6 : 2));
            Assert.That(metrics.RadixPartitions, Is.Zero);
            Assert.That(TrieUpdater.UpdateRoot(reopened, preparedRoot, batch.Build()), Is.EqualTo(preparedRoot));
        }
    }

    [Test]
    public void Prepared_mixed_partition_batches_match_generic_and_reopen(
        [Values(0, 1, 2, 3, 4, 16, 17, 31, 32, 33, 256)] int count,
        [Values(1, 16, 256)] int shards,
        [Values(false, true)] bool fallback)
    {
        using PbtNodeGroupStore preparedStore = new();
        using PbtNodeGroupStore genericStore = new();
        EipReferenceTree oracle = new();
        PbtStorageFullKey[] keys = new PbtStorageFullKey[count];
        for (int index = 0; index < count; index++)
        {
            byte zone = index % 3 == 2 ? (byte)0xFF : (byte)(index % 3);
            byte[] key = new byte[(zone == 0xFF ? 65 : 34) + index % 2];
            key[0] = zone;
            key[1] = (byte)(index % shards);
            key[^3] = (byte)(index >> 8);
            key[^2] = (byte)index;
            key[^1] = 1;
            keys[index] = new(key);
        }

        ValueHash256 preparedRoot = default;
        ValueHash256 genericRoot = default;
        for (int round = 0; round < 3; round++)
        {
            using PbtWriteBatchBuilder<PbtStorageFullKey> batch = new(0);
            for (int index = count - 1; index >= 0; index--)
            {
                // Leave one zone untouched during the mixed update, then remove everything.
                if (round == 1 && index % 3 == 1) continue;
                PbtStorageFullKey key = keys[index];
                batch.Set(key, new ValueHash256(Value(1)));
                batch.Delete(key);
                if (round == 0 || (round == 1 && index % 2 == 0))
                    batch.Set(key, new ValueHash256(Value((byte)(round + 2))));
            }
            if (round == 1)
                batch.Delete(new PbtStorageFullKey(Bytes.FromHexString("0x00000000000000000000000000000000000000000000000000000000000000000000FF")));
            if (fallback)
                batch.Set(new PbtStorageFullKey(Bytes.FromHexString("0x42")), new ValueHash256(Value(4)));

            foreach (PbtWriteOperation<PbtStorageFullKey> operation in batch.Operations)
            {
                if (operation.Value == default) oracle.Delete(operation.Key.Bytes);
                else oracle.Insert(operation.Key.Bytes, operation.Value.Bytes.ToArray());
            }
            using PbtWriteBatchSet<PbtStorageFullKey> prepared = PbtWriteBatchSet<PbtStorageFullKey>.Create(batch.Build());
            if (!prepared.Precalculated.IsEmpty) AssertPreparedLevel(prepared.Entries, prepared.Precalculated, 0);
            preparedRoot = TrieUpdater.UpdateRoot(preparedStore, preparedRoot, prepared);
            genericRoot = TrieUpdater.UpdateRoot(genericStore, genericRoot, batch.Build());
            IReadOnlyList<PbtPhysicalPayload> preparedPayloads = preparedStore.ExportPhysicalPayloads();
            IReadOnlyList<PbtPhysicalPayload> genericPayloads = genericStore.ExportPhysicalPayloads();
            using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(preparedPayloads);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(preparedRoot, Is.EqualTo(genericRoot), $"round {round}");
                Assert.That(preparedRoot.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"round {round}");
                Assert.That(preparedPayloads.Count, Is.EqualTo(genericPayloads.Count));
                Assert.That(TrieUpdater.UpdateRoot(reopened, preparedRoot, PbtWriteBatchSet<PbtStorageFullKey>.Create(batch.Build())), Is.EqualTo(preparedRoot), "reprepare and replay after reopen");
            }
            for (int index = 0; index < preparedPayloads.Count; index++)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(preparedPayloads[index].Key.ToArray(), Is.EqualTo(genericPayloads[index].Key.ToArray()));
                    Assert.That(preparedPayloads[index].Payload.ToArray(), Is.EqualTo(genericPayloads[index].Payload.ToArray()));
                }
            }
        }
    }

    [Test]
    public void Prepared_prefix_replacement_and_conflict_preserve_terminal_semantics(
        [Values(false, true)] bool shorterReplacement,
        [Values(false, true)] bool conflict,
        [Values(false, true)] bool persisted,
        [Values(false, true)] bool sibling,
        [Values(false, true)] bool zeroDeletes)
    {
        byte[] shortKey = Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000000001");
        byte[] longKey = new byte[shortKey.Length + 1];
        shortKey.CopyTo(longKey, 0);
        PbtStorageFullKey original = new(shorterReplacement ? longKey : shortKey);
        PbtStorageFullKey replacement = new(shorterReplacement ? shortKey : longKey);
        byte[] siblingBytes = original.Bytes.ToArray();
        siblingBytes[^1] ^= 1;
        PbtStorageFullKey siblingKey = new(siblingBytes);
        using PbtWriteBatchBuilder<PbtStorageFullKey> initial = new(0);
        if (persisted)
        {
            initial.Set(original, new ValueHash256(Value(1)));
            if (sibling) initial.Set(siblingKey, new ValueHash256(Value(3)));
        }
        using PbtNodeGroupStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, initial.Build());
        using PbtWriteBatchBuilder<PbtStorageFullKey> changes = new(0);
        if (!conflict)
        {
            if (zeroDeletes)
            {
                changes.Set(original, default);
                if (sibling) changes.Set(siblingKey, default);
            }
            else
            {
                changes.Delete(original);
                if (sibling) changes.Delete(siblingKey);
            }
        }
        else if (!persisted)
        {
            changes.Set(original, new ValueHash256(Value(1)));
            if (sibling) changes.Set(siblingKey, new ValueHash256(Value(3)));
        }
        changes.Set(replacement, new ValueHash256(Value(2)));
        if (conflict)
        {
            TrackingMemoryProvider memoryProvider = new();
            Assert.Throws<ArgumentException>(() => TrieUpdater.UpdateRoot(store, root, PbtWriteBatchSet<PbtStorageFullKey>.Create(changes.Build()), memoryProvider: memoryProvider));
            store.Dispose();
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
            return;
        }

        ValueHash256 result = TrieUpdater.UpdateRoot(store, root, PbtWriteBatchSet<PbtStorageFullKey>.Create(changes.Build()));
        EipReferenceTree oracle = new();
        oracle.Insert(replacement.Bytes, Value(2));
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.ExportPhysicalPayloads());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(TrieUpdater.UpdateRoot(reopened, result, PbtWriteBatchSet<PbtStorageFullKey>.Create(changes.Build())), Is.EqualTo(result));
        }
    }

    private static void AssertPreparedLevel<TKey>(ReadOnlySpan<PbtWriteOperation<TKey>> entries, ReadOnlySpan<int> table, int depth) where TKey : struct, IPbtKey<TKey>
    {
        Assert.That(table.Length, Is.EqualTo(17));
        int[] counts = new int[16];
        foreach (PbtWriteOperation<TKey> entry in entries) counts[(entry.Key.Bytes[depth / 8] >> (4 - depth % 8)) & 15]++;
        int expectedMask = 0;
        int countIndex = 1;
        int offset = 0;
        for (int slot = 0; slot < 16; slot++)
        {
            if (counts[slot] == 0) continue;
            expectedMask |= 1 << slot;
            Assert.That(table[countIndex++], Is.EqualTo(counts[slot]));
            ReadOnlySpan<PbtWriteOperation<TKey>> bucket = entries.Slice(offset, counts[slot]);
            foreach (PbtWriteOperation<TKey> entry in bucket)
                Assert.That((entry.Key.Bytes[depth / 8] >> (4 - depth % 8)) & 15, Is.EqualTo(slot));
            offset += counts[slot];
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(table[0], Is.EqualTo(expectedMask));
            Assert.That(offset, Is.EqualTo(entries.Length));
            for (; countIndex <= 16; countIndex++) Assert.That(table[countIndex], Is.Zero);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Bucket_plan_transitions_keep_only_valid_range_knowledge(bool preservesOrder)
    {
        int[] table = new int[17];
        table[0] = 1 << 3;
        table[1] = 2;
        TrieUpdater.BucketPlan plan = new(table, 0, 4, true, true);
        TrieUpdater.BucketPlan known = plan.WithRangeKnowledge(7, true);
        TrieUpdater.BucketPlan child = known.ForChild();
        TrieUpdater.BucketPlan jumped = known.AfterJump(4);
        TrieUpdater.BucketPlan filtered = known.AfterFiltering(preservesOrder);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(child.Precalculated.IsEmpty, Is.True);
            Assert.That(child.Depth, Is.EqualTo(4));
            Assert.That(child.BranchDepth, Is.EqualTo(7));
            Assert.That(child.IsSorted && child.PrefixesValidated, Is.True);
            Assert.That(jumped.Precalculated.IsEmpty, Is.True);
            Assert.That(jumped.Depth, Is.EqualTo(4));
            Assert.That(jumped.BranchDepth, Is.EqualTo(7));
            Assert.That(jumped.IsSorted && jumped.PrefixesValidated, Is.True);
            Assert.That(filtered.Precalculated.IsEmpty, Is.True);
            Assert.That(filtered.Depth, Is.Zero);
            Assert.That(filtered.BranchDepth, Is.EqualTo(7));
            Assert.That(filtered.IsSorted, Is.EqualTo(preservesOrder));
            Assert.That(filtered.PrefixesValidated, Is.True);
        }
    }

    [Test]
    public void Single_bucket_prefix_is_reused_across_sort_levels(
        [Values(2, 3, 16, 17, 33)] int count,
        [Values(0, 4)] int depth,
        [Values(false, true)] bool sorted)
    {
        PbtWriteOperation<PbtStorageFullKey>[] operations = new PbtWriteOperation<PbtStorageFullKey>[count];
        for (int index = 0; index < count; index++)
        {
            byte[] key = Bytes.FromHexString("0xAAAAAAAA0000");
            key[^1] = (byte)(sorted ? index : count - index - 1);
            operations[index] = PbtWriteOperation<PbtStorageFullKey>.Set(new PbtStorageFullKey(key), new ValueHash256(Value(1)));
        }
        TrieUpdaterMetrics metrics = new();
        TrieUpdater.BucketPlan plan = new(default, depth, depth, sorted, false);
        byte[] buffer = new byte[plan.GetBufferSize(count)];
        TrieUpdater.PartitionOutcome outcome = plan.WithBuffer(buffer).BucketSort(operations, metrics);
        int branchDepth = operations[0].Key.BitLength;
        foreach (PbtWriteOperation<PbtStorageFullKey> operation in operations)
            branchDepth = Math.Min(branchDepth, operations[0].Key.FirstDifferingBit(operation.Key));
        int comparisons = metrics.OperationPrefixComparisons;
        int partitions = metrics.RadixPartitions;
        int sorts = metrics.FullKeySorts;
        int synthesized = 0;
        Assert.That(outcome.Plan.BranchDepth, Is.EqualTo(branchDepth));
        for (int childDepth = depth + 4; childDepth + 4 <= branchDepth; childDepth += 4)
        {
            plan = outcome.Plan.ForChild();
            buffer = new byte[plan.GetBufferSize(count)];
            outcome = plan.WithBuffer(buffer).BucketSort(operations, metrics);
            synthesized++;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(System.Numerics.BitOperations.IsPow2(outcome.UsedMask), Is.True);
                Assert.That(outcome.Plan.BranchDepth, Is.EqualTo(branchDepth));
                Assert.That(buffer.Length, Is.EqualTo(sizeof(int)));
                Assert.That(outcome.Counts.ToArray(), Is.EqualTo(new[] { count }));
                Assert.That(metrics.OperationPrefixComparisons, Is.EqualTo(comparisons));
                Assert.That(metrics.RadixPartitions, Is.EqualTo(partitions));
                Assert.That(metrics.FullKeySorts, Is.EqualTo(sorts));
            }
        }
        Assert.That(metrics.SynthesizedSingleBuckets, Is.EqualTo(synthesized));
    }

    [TestCase(17)]
    [TestCase(33)]
    public void Single_bucket_prefix_survives_existing_sibling_branches(int count)
    {
        List<(byte[] Key, byte[]? Value)> initial = BoundaryChanges(count, 40, 16, 2);
        initial.Add((Bytes.FromHexString("0xAA8000000000"), Value(1)));
        initial.Add((Bytes.FromHexString("0xAAAA80000000"), Value(2)));
        initial.Add((Bytes.FromHexString("0xAAAAAA800000"), Value(3)));
        List<(byte[] Key, byte[]? Value)> changes = [];
        using PbtWriteBatchBuilder<PbtStorageFullKey> builder = new(1);
        for (int index = 0; index < count; index++)
        {
            byte[] key = initial[index].Key;
            changes.Add((key, Value(0xEF)));
            builder.Set(new PbtStorageFullKey(key), new ValueHash256(Value(0xEF)));
        }
        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        ApplyAll(bulk, serial, oracle, initial);
        ApplyAll(bulk, serial, oracle, changes);
        AssertEquivalentAfterReopen(bulk, serial, oracle, "single bucket with shallower siblings");
        CountingPbtStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(initial.ToArray()));
        TrieUpdaterMetrics metrics = new();
        root = TrieUpdater.UpdateRoot(store, root, builder.Build(), metrics);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(bulk.RootHash));
            Assert.That(metrics.RadixPartitions, Is.EqualTo(2));
            Assert.That(metrics.SynthesizedSingleBuckets, Is.GreaterThan(1));
        }
        AssertAllMemoryReleased(store);
    }

    [TestCase(2)]
    [TestCase(3)]
    [TestCase(16)]
    [TestCase(17)]
    [TestCase(33)]
    public void Bucket_plan_dispatch_preserves_full_key_sortedness(int count)
    {
        PbtWriteOperation<PbtStorageFullKey>[] operations = new PbtWriteOperation<PbtStorageFullKey>[count];
        for (int index = 0; index < count; index++)
        {
            byte[] key = Bytes.FromHexString("0x0000");
            key[0] = (byte)((index % 2 == 0 ? 0x10 : 0xF0) | ((count - index) & 15));
            key[1] = (byte)index;
            operations[index] = PbtWriteOperation<PbtStorageFullKey>.Set(new PbtStorageFullKey(key), new ValueHash256(Value(1)));
        }
        TrieUpdaterMetrics metrics = new();
        TrieUpdater.BucketPlan plan = default;
        byte[] buffer = new byte[plan.GetBufferSize(count)];
        TrieUpdater.PartitionOutcome outcome = plan.WithBuffer(buffer).BucketSort(operations, metrics);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Plan.IsSorted, Is.EqualTo(count <= 16));
            Assert.That(metrics.FullKeySorts, Is.EqualTo(count <= 16 ? 1 : 0));
            Assert.That(metrics.RadixPartitions, Is.EqualTo(count > 16 ? 1 : 0));
        }
        if (count <= 16)
        {
            for (int index = 1; index < count; index++) Assert.That(operations[index - 1].Key.CompareTo(operations[index].Key), Is.LessThan(0));
            int length = outcome.Counts[0];
            TrieUpdater.BucketPlan childPlan = outcome.Plan.ForChild();
            byte[] childBuffer = new byte[childPlan.GetBufferSize(length)];
            TrieUpdater.PartitionOutcome child = childPlan.WithBuffer(childBuffer).BucketSort(operations.AsSpan(0, length), metrics);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(child.Plan.IsSorted, Is.True);
                Assert.That(metrics.FullKeySorts, Is.EqualTo(1));
                Assert.That(metrics.SortedLevels, Is.EqualTo(length > 1 ? 1 : 0));
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Variable_length_prefix_conflict_is_not_hidden_by_parent_validation(bool reverse)
    {
        List<(byte[] Key, byte[]? Value)> writes =
        [
            (Bytes.FromHexString("0x00"), Value(1)),
            (Bytes.FromHexString("0x80"), Value(2)),
            (Bytes.FromHexString("0x8000"), Value(3)),
        ];
        if (reverse) writes.Reverse();
        using PbtTreeHarness tree = new();
        Assert.Throws<ArgumentException>(() => tree.ApplyBatch(writes));
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
            ApplyAll(bulk, serial, oracle, [(Bytes.FromHexString("0x555555000002"), Value(0x77))]);

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
            byte[] key = Bytes.FromHexString("0xAAAAAA000000");
            int destination = occupiedSlots == 1 ? 15 : occupiedSlots == 2 ? (index % 2) * 15 : index % 16;
            int shift = 4 - groupDepth % 8;
            key[groupDepth / 8] = (byte)((key[groupDepth / 8] & ~(15 << shift)) | (destination << shift));
            key[3] = (byte)(index >> 8);
            key[4] = (byte)index;
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
    public void All_second_group_boundary_destinations_fetch_only_touched_groups()
    {
        CountingPbtStore store = new();
        using PbtWriteBatchBuilder<PbtStorageFullKey> initial = new(0);
        using PbtWriteBatchBuilder<PbtStorageFullKey> changes = new(0);
        for (int destination = 0; destination < 16; destination++)
        {
            PbtStorageFullKey key = new([(byte)(0xA0 | destination), 0x11]);
            initial.Set(key, new ValueHash256(Value((byte)(destination + 1))));
            changes.Set(key, new ValueHash256(Value((byte)(0x40 + destination))));
        }
        PbtStorageFullKey untouchedKey = new([0xB1, 0x11]);
        initial.Set(untouchedKey, new ValueHash256(Value(0xEE)));

        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, initial.Build());
        store.ResetReads();
        TrieUpdaterMetrics metrics = new();
        ValueHash256 changedRoot = TrieUpdater.UpdateRoot(store, root, changes.Build(), metrics);
        PbtStorageNodePath untouchedGroup = new([0xB0], 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changedRoot, Is.Not.EqualTo(root));
            Assert.That(store.GroupReads.ContainsKey(untouchedGroup), Is.False);
            Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(store.Reads));
            Assert.That(metrics.GroupParses, Is.EqualTo(store.Reads));
            Assert.That(metrics.EmittedNodeWrites, Is.EqualTo(store.LastNodeWrites));
        }
    }

    private static IEnumerable<TestCaseData> DenseGroupCollapseCases()
    {
        int[] groupDepths = [0, 4, 8, 12, 248, 252, 520, 524];
        int[] retainedMasks = [0x0000, 0x0001, 0x8000, 0x000A, 0xA000, 0xA55A, 0x8001, 0xFFFF];
        foreach (int groupDepth in groupDepths)
        foreach (int retainedMask in retainedMasks)
            yield return new TestCaseData(groupDepth, retainedMask)
                .SetName($"Dense_group_paths_survive_collapse_and_restoration(depth={groupDepth}, retained=0x{retainedMask:X4})");
    }

    [TestCaseSource(nameof(DenseGroupCollapseCases))]
    public void Dense_group_paths_survive_collapse_and_restoration(int groupDepth, int retainedMask)
    {
        byte[] sharedKey = new byte[(groupDepth + 4 + 7) >> 3];
        new Random(8297).NextBytes(sharedKey);
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
        byte[] leftKey = Bytes.FromHexString("0x0000");
        byte[] existingRightKey = Bytes.FromHexString("0x0008");
        byte[] insertedKey = Bytes.FromHexString("0x0000");
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
    [TestCase("shorter-terminal-replacement")]
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
        byte[] deleteKeyBytes = [0x00];
        byte[] setKeyBytes = [0x80];
        ValueHash256 root = deleteKeyExists
            ? TrieUpdater.UpdateRoot(store, default, Batch((deleteKeyBytes, Value(1))))
            : default;

        ValueHash256 rootAfterUpdate = TrieUpdater.UpdateRoot(store, root, Batch(
            (deleteKeyBytes, null), (setKeyBytes, Value(2))));

        byte[] expectedLeaf = PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(setKeyBytes), Value(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootAfterUpdate, Is.EqualTo(PbtNodeCodec.Hash(new PbtNodeReader(expectedLeaf))));
            Assert.That(store.Inner.EnumerateRecords(), Has.Count.EqualTo(1));
            Assert.That(store.GetNode(new PbtStorageNodePath([], 0)), Is.EqualTo(expectedLeaf));
        }
    }

    [Test]
    public void Shared_operation_prefix_is_compared_once_across_depth_jumps(
        [Values(3, 4, 7, 8, 13, 128, 260)] int divergenceBit,
        [Values(false, true)] bool persisted)
    {
        byte[] leftKey = new byte[(divergenceBit + 8) >> 3];
        byte[] rightKey = new byte[leftKey.Length + 1];
        rightKey[divergenceBit >> 3] = (byte)(1 << (7 - (divergenceBit & 7)));
        byte[] untouchedKey = Bytes.FromHexString("0x80");
        List<(byte[] Key, byte[]? Value)> initial = persisted
            ? [(leftKey, Value(1)), (rightKey, Value(2)), (untouchedKey, Value(3))]
            : [];
        List<(byte[] Key, byte[]? Value)> changes = [(leftKey, Value(4)), (rightKey, Value(5))];
        CountingPbtStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(initial.ToArray()));
        store.ResetReads();
        TrieUpdaterMetrics metrics = new();
        root = TrieUpdater.UpdateRoot(store, root, Batch(changes.ToArray()), metrics);

        using PbtTreeHarness bulk = new();
        using PbtTreeHarness serial = new();
        EipReferenceTree oracle = new();
        ApplyAll(bulk, serial, oracle, initial);
        ApplyAll(bulk, serial, oracle, changes);
        AssertEquivalentAfterReopen(bulk, serial, oracle, $"prefix at bit {divergenceBit}, persisted {persisted}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(bulk.RootHash));
            Assert.That(metrics.OperationPrefixComparisons, Is.EqualTo(persisted && divergenceBit < 8 ? 0 : 1));
            Assert.That(metrics.SynthesizedSingleBuckets, Is.Zero);
            Assert.That(metrics.PrecalculatedLevels, Is.EqualTo(1));
            Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(store.Reads));
            Assert.That(metrics.EmittedNodeWrites, Is.EqualTo(store.LastNodeWrites));
        }
        AssertAllMemoryReleased(store);
    }

    [Test]
    public void Mixed_bulk_updates_sharing_a_long_persisted_prefix_load_without_node_reads()
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
            Assert.That(store.GroupReads, Is.Not.Empty);
            Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(store.Reads));
            Assert.That(metrics.GroupParses, Is.LessThanOrEqualTo(metrics.PhysicalGroupFetches));
            Assert.That(metrics.GroupFrameResolutions, Is.GreaterThanOrEqualTo(metrics.PhysicalGroupFetches));
            Assert.That(metrics.EmittedNodeWrites, Is.EqualTo(store.LastNodeWrites));
        }

        AssertAllMemoryReleased(store);
    }

    [Test]
    public void Empty_batch_returns_supplied_root_without_storage_access()
    {
        CountingPbtStore store = new();
        ValueHash256 suppliedRoot = new(Value(0xEE));
        ValueHash256 result = TrieUpdater.UpdateRoot(store, suppliedRoot, Batch());

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
        ValueHash256 actualRoot = TrieUpdater.UpdateRoot(source, default, Batch(
            ([0x12], Value(1)), ([0x92], Value(2)), ([0xF0], Value(3))));
        PbtPhysicalPayload[] initialPayloads = [.. source.ExportPhysicalPayloads()];
        ValueHash256 staleRoot = useDefaultRoot ? default : new ValueHash256(Value(0xEE));
        using PbtWriteBatchBuilder<PbtStorageFullKey> changes = BatchBuilder(([0x12], Value(4)), ([0xA0], Value(5)));

        using PbtNodeGroupStore staleStore = PbtNodeGroupStore.FromPhysicalPayloads(initialPayloads);
        using PbtNodeGroupStore actualStore = PbtNodeGroupStore.FromPhysicalPayloads(initialPayloads);
        ValueHash256 resultFromStaleRoot = TrieUpdater.UpdateRoot(staleStore, staleRoot, changes.Build());
        ValueHash256 resultFromActualRoot = TrieUpdater.UpdateRoot(actualStore, actualRoot, changes.Build());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resultFromStaleRoot, Is.EqualTo(resultFromActualRoot));
            Assert.That(CanonicalRecords(staleStore), Is.EqualTo(CanonicalRecords(actualStore)));
            Assert.That(PhysicalRecords(staleStore.ExportPhysicalPayloads()), Is.EqualTo(PhysicalRecords(actualStore.ExportPhysicalPayloads())));
        }
    }

    [Test]
    public void Same_group_recursion_uses_one_frame_and_suppresses_noop_node_writes()
    {
        CountingPbtStore store = new();
        using PbtWriteBatchBuilder<PbtStorageFullKey> initial = BatchBuilder(([0x00], Value(1)), ([0x40], Value(2)));
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, initial.Build());
        store.ResetReads();
        TrieUpdaterMetrics metrics = new();

        ValueHash256 unchangedRoot = TrieUpdater.UpdateRoot(store, root, initial.Build(), metrics);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unchangedRoot, Is.EqualTo(root));
            Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(1));
            Assert.That(metrics.GroupParses, Is.EqualTo(1));
            Assert.That(metrics.GroupFrameResolutions, Is.EqualTo(1), "same-group logical nodes use the active frame");
            Assert.That(metrics.EmittedNodeWrites, Is.Zero);
            Assert.That(store.LastNodeWrites, Is.Zero);
        }

        AssertAllMemoryReleased(store);
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

        PbtStorageNodePath untouchedGroup = new([0x80], 4);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(2), "root group and changed left boundary group");
            Assert.That(metrics.GroupParses, Is.EqualTo(2));
            Assert.That(metrics.GroupFrameResolutions, Is.EqualTo(2), "one frame resolution per entered physical group");
            Assert.That(store.GroupReads.Values, Has.All.EqualTo(1));
            Assert.That(store.GroupReads.ContainsKey(untouchedGroup), Is.False, "the untouched right group is not fetched");
            Assert.That(metrics.EmittedNodeWrites, Is.EqualTo(store.LastNodeWrites));
        }
    }

    [TestCase(false, 0)]
    [TestCase(false, 1)]
    [TestCase(true, 0)]
    public void Group_leases_are_released_when_decode_or_apply_fails(bool applyFailure, int malformedPayloadLength)
    {
        CountingPbtStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(([0x12], Value(1))));
        int appliesBeforeFailure = store.Applies;
        if (applyFailure) store.ThrowOnApply = true;
        else
        {
            store.OverrideGroup = _ =>
            {
                RefCountingMemory memory = store.MemoryProvider.Rent(malformedPayloadLength);
                if (malformedPayloadLength != 0) memory.GetSpan()[0] = 0x01;
                return memory;
            };
        }

        TrackingMemoryProvider memoryProvider = new();
        Action update = () => TrieUpdater.UpdateRoot(store, root, Batch(([0x12], Value(2))), null, memoryProvider);
        if (applyFailure) Assert.Throws<InvalidOperationException>(update);
        else Assert.Throws<InvalidDataException>(update);

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
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(([0x12], Value(1)), ([0x92], Value(2))));
        int appliesBeforeFailure = store.Applies;
        store.OverrideNode = path => path.BitDepth == 0 ? store.Inner.GetNode(path) : null;

        TrackingMemoryProvider memoryProvider = new();
        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, root, Batch(([0x12], Value(3))), null, memoryProvider));

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
    public void Failed_batches_never_apply_or_change_state_when_a_referenced_node_is_missing()
    {
        CountingPbtStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(([0x12], Value(1)), ([0x92], Value(2))));
        PbtPhysicalPayload[] before = [.. store.Inner.ExportPhysicalPayloads()];
        int appliesBeforeFailure = store.Applies;
        store.OverrideNode = path => path.BitDepth == 0 ? store.Inner.GetNode(path) : null;

        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, root, Batch(([0x12], Value(3)))));

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
        byte[][] keys = [Bytes.FromHexString("00123450"), Bytes.FromHexString("00123458"), Bytes.FromHexString("00123800"), Bytes.FromHexString("01123450"), Bytes.FromHexString("ff123450")];
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
            expectedRoot = TrieUpdater.UpdateRoot(expectedStore, expectedRoot, Batch(changes));
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
            (Bytes.FromHexString("00123450"), Value(1)), (Bytes.FromHexString("00123458"), Value(2)),
            (Bytes.FromHexString("01123450"), Value(3)), (Bytes.FromHexString("01123458"), Value(4)),
            (Bytes.FromHexString("ff123450"), Value(5)), (Bytes.FromHexString("ff123458"), Value(6)),
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
    [TestCase(false, 8)]
    [TestCase(true, 0)]
    [TestCase(true, 8)]
    public void Owned_node_encodings_are_released_when_worker_or_ancestor_publish_fails(bool parallel, int failedDepth)
    {
        TrackingMemoryProvider memoryProvider = new();
        TrackingMemoryProvider storeProvider = new();
        using PbtNodeGroupStore innerStore = new(storeProvider);
        FailingPublishStore store = new(innerStore, failedDepth);
        (byte[] Key, byte[]? Value)[] changes =
        [
            (Bytes.FromHexString("00000000"), Value(1)), (Bytes.FromHexString("00800000"), Value(2)),
            (Bytes.FromHexString("01000000"), Value(3)), (Bytes.FromHexString("01800000"), Value(4)),
            (Bytes.FromHexString("ff000000"), Value(5)), (Bytes.FromHexString("ff800000"), Value(6)),
        ];
        Assert.Catch(() => ApplyTracked(store, default, changes, parallel, memoryProvider));
        AssertOnlyPublishedRentalsRemain(innerStore, memoryProvider);
        innerStore.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        Assert.That(TrackingMemoryProvider.CountUnreleased(storeProvider.Rented), Is.Zero);
    }

    private sealed class FailingPublishStore(IPbtStore store, int failedDepth) : IPbtStore
    {
        public RefCountingMemory? GetNodeGroup(IPbtNodePath groupKey) => store.GetNodeGroup(groupKey);
        public void SetNodeGroup(IPbtNodePath groupKey, RefCountingMemory? payload)
        {
            if (groupKey.BitDepth == failedDepth) throw new InvalidOperationException("Configured publish failure.");
            store.SetNodeGroup(groupKey, payload);
        }
    }

    [Test]
    public void Promoted_subtree_retains_read_lease_after_its_frame_is_disposed()
    {
        CountingPbtStore store = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch(
            (Bytes.FromHexString("123450"), Value(1)), (Bytes.FromHexString("123458"), Value(2)), (Bytes.FromHexString("80"), Value(3))));
        TrackingMemoryProvider readProvider = new();
        TrackingMemoryProvider nodeProvider = new() { FillByte = 0xFF };
        RefCountingMemory? promotedPayload = null;
        bool checkedPromotion = false;
        store.OverrideGroup = groupKey =>
        {
            using RefCountingMemory? stored = store.Inner.GetNodeGroup(groupKey);
            if (stored is null) return null;
            RefCountingMemory read = readProvider.Rent(stored.Memory.Length);
            stored.GetSpan().CopyTo(read.GetSpan());
            if (groupKey.BitDepth == 20) promotedPayload = read;
            return read;
        };
        store.OnApply = groupKey =>
        {
            if (groupKey.BitDepth != 0) return;
            Assert.That(promotedPayload, Is.Not.Null);
            Assert.That(TrackingMemoryProvider.CountUnreleased([promotedPayload!]), Is.Zero);
            checkedPromotion = true;
        };
        root = TrieUpdater.UpdateRoot(store, root, Batch((Bytes.FromHexString("123458"), null)), null, nodeProvider);
        EipReferenceTree oracle = new();
        oracle.Insert(Bytes.FromHexString("123450"), Value(1));
        oracle.Insert(Bytes.FromHexString("80"), Value(3));
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

    [TestCase(2, 2)]
    [TestCase(16, 4)]
    public void Ordered_group_emission_rents_geometrically_instead_of_per_node(int leafCount, int expectedRentCount)
    {
        const int leafEncodingLength = 3 + 1 + 32;
        const int rootEncodingLength = 3 + 2 * 32;
        TrackingMemoryProvider provider = new() { FillByte = 0xFF };
        using PbtNodeGroupStore store = new();
        EipReferenceTree oracle = new();
        (byte[] Key, byte[]? Value)[] changes = new (byte[], byte[]?)[leafCount];
        for (int index = 0; index < leafCount; index++)
        {
            byte[] key = Bytes.FromHexString((index * 256 / leafCount).ToString("X2"));
            changes[index] = (key, Value((byte)(index + 1)));
            oracle.Insert(key, changes[index].Value!);
        }
        ValueHash256 root = ApplyTracked(store, default, changes, false, provider);
        IReadOnlyList<PbtPhysicalPayload> payloads = store.ExportPhysicalPayloads();
        Assert.That(payloads, Has.Count.EqualTo(1));
        IPbtNodePath groupKey = new PbtStorageNodePath([], 0);
        PbtNodeGroupReader reader = new(groupKey, payloads[0].Payload.Span);
        List<PbtNodeRecord> records = [];
        PbtNodeGroupReader.Enumerator nodes = reader.EnumerateNodes();
        while (nodes.MoveNext())
            records.Add(new(PbtFourLevelGroupGeometry.PathOf(groupKey, nodes.CurrentPosition), nodes.Current));
        byte[] expectedPayload = new byte[payloads[0].Payload.Length];
        BufferWriter writer = new(expectedPayload);
        PbtNodeGroupCodec.Encode(ref writer, groupKey, records);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(reader.Count, Is.EqualTo(leafCount + 1));
            Assert.That(payloads[0].Payload.Length, Is.EqualTo(PbtNodeGroupCodec.HeaderLength + leafCount * leafEncodingLength + rootEncodingLength + PbtNodeGroupCodec.TrailerLength));
            Assert.That(payloads[0].Payload.ToArray(), Is.EqualTo(expectedPayload));
            Assert.That(provider.RentCount, Is.EqualTo(expectedRentCount));
            int firstRentLength = PbtNodeGroupCodec.HeaderLength + leafEncodingLength + PbtNodeGroupCodec.TrailerLength;
            for (int rental = 0; rental < provider.RequestedLengths.Count; rental++)
                Assert.That(provider.RequestedLengths[rental], Is.EqualTo(firstRentLength << rental));
            AssertOnlyPublishedRentalsRemain(store, provider);
        }
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(payloads);
        Assert.That(PhysicalRecords(reopened.ExportPhysicalPayloads()), Is.EqualTo(PhysicalRecords(payloads)));
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero);
    }

    [TestCase("AA00", "AA80", "B000", "AA", 8)]
    [TestCase("AAC0", "AAE0", "B000", "AAC0", 10)]
    public void Ordered_emission_removes_old_branch_position_when_hoisting_to_root(
        string leftHex, string rightHex, string siblingHex, string prefixHex, int prefixBits)
    {
        using PbtNodeGroupStore store = new();
        byte[] left = Bytes.FromHexString(leftHex), right = Bytes.FromHexString(rightHex), sibling = Bytes.FromHexString(siblingHex);
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, Batch((left, Value(1)), (right, Value(2)), (sibling, Value(3))));
        IPbtNodePath groupKey = new PbtStorageNodePath([], 0);
        using (RefCountingMemory? original = store.GetNodeGroup(groupKey))
        {
            PbtNodeGroupReader reader = new(groupKey, original!.GetSpan());
            PbtNodeReader branch = new(reader[18]);
            Assert.That(branch.PrefixBitCount, Is.EqualTo(prefixBits - 4));
        }
        root = TrieUpdater.UpdateRoot(store, root, Batch((sibling, null)));
        EipReferenceTree oracle = new();
        oracle.Insert(left, Value(1));
        oracle.Insert(right, Value(2));
        using RefCountingMemory? updated = store.GetNodeGroup(groupKey);
        PbtNodeGroupReader updatedReader = new(groupKey, updated!.GetSpan());
        PbtNodeReader promoted = new(updatedReader[30]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(updatedReader.Availability, Is.EqualTo(1u << 30));
            Assert.That(promoted.PrefixBitCount, Is.EqualTo(prefixBits));
            Assert.That(promoted.Prefix.ToArray(), Is.EqualTo(Bytes.FromHexString(prefixHex)));
        }
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.ExportPhysicalPayloads());
        Assert.That(TrieUpdater.UpdateRoot(reopened, root, Batch((left, Value(1)))), Is.EqualTo(root));
    }

    private static void AssertOnlyPublishedRentalsRemain(PbtNodeGroupStore store, TrackingMemoryProvider provider, bool allPublishedAreTracked = true)
    {
        HashSet<RefCountingMemory> published = [];
        foreach (IPbtNodePath groupKey in store.EnumerateNodeGroupKeys())
        {
            using RefCountingMemory? payload = store.GetNodeGroup(groupKey);
            published.Add(payload!);
        }
        foreach (RefCountingMemory rental in provider.Rented)
            Assert.That(TrackingMemoryProvider.CountUnreleased([rental]), Is.EqualTo(published.Contains(rental) ? 1 : 0));
        if (allPublishedAreTracked)
            Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.EqualTo(published.Count));
    }

    private static ValueHash256 ApplyTracked(IPbtStore store, ValueHash256 root, (byte[] Key, byte[]? Value)[] changes, bool parallel, TrackingMemoryProvider memoryProvider)
    {
        if (!parallel) return TrieUpdater.UpdateRoot(store, root, Batch(changes), null, memoryProvider);
        using PbtPartitionBatches partitions = PbtStoreTestExtensions.PreparePartitions(changes);
        return TrieUpdater.UpdateRoot(store, root, partitions, null, memoryProvider);
    }

    private static (List<(byte[] Key, byte[]? Value)> Initial, List<(byte[] Key, byte[]? Value)> Changes) Scenario(string name) => name switch
    {
        "shorter-terminal-replacement" => (
            [(Bytes.FromHexString("0x123400"), Value(1)), (Bytes.FromHexString("0x123480"), Value(2)), (Bytes.FromHexString("0x80"), Value(3))],
            [(Bytes.FromHexString("0x123400"), null), (Bytes.FromHexString("0x123480"), null), (Bytes.FromHexString("0x1234"), Value(4))]),
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
            if (value is null || new ValueHash256(value) == default) oracle.Delete(key);
            else oracle.Insert(key, value);
        }
    }

    private static PbtWriteBatch<PbtStorageFullKey> Batch(params (byte[] Key, byte[]? Value)[] changes)
    {
        using PbtWriteBatchBuilder<PbtStorageFullKey> builder = BatchBuilder(changes);
        return builder.Build();
    }

    private static PbtWriteBatchBuilder<PbtStorageFullKey> BatchBuilder(params (byte[] Key, byte[]? Value)[] changes)
    {
        PbtWriteBatchBuilder<PbtStorageFullKey> batch = new(0);
        foreach ((byte[] key, byte[]? value) in changes)
        {
            PbtStorageFullKey fullKey = new(key);
            if (value is null) batch.Delete(fullKey);
            else batch.Set(fullKey, new ValueHash256(value));
        }
        return batch;
    }

    private static string[] CanonicalRecords(PbtNodeGroupStore store)
    {
        List<string> records = [];
        foreach (PbtNodeRecord record in store.EnumerateRecords())
            records.Add(Convert.ToHexString(record.Path.Encode()) + Convert.ToHexString(record.Encoding.Span));
        return [.. records];
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

    private static void AssertAllMemoryReleased(CountingPbtStore store)
    {
        store.Inner.Dispose();
        Assert.That(store.UnreleasedMemoryCount, Is.Zero);
    }

    private sealed class CountingPbtStore : IPbtStore
    {
        internal TrackingMemoryProvider MemoryProvider { get; } = new();
        internal PbtNodeGroupStore Inner { get; }

        internal CountingPbtStore() => Inner = new(MemoryProvider);

        internal int UnreleasedMemoryCount => TrackingMemoryProvider.CountUnreleased(MemoryProvider.Rented);
        internal int Reads { get; private set; }
        internal int Applies { get; private set; }
        internal int LastNodeWrites { get; private set; }
        internal Dictionary<IPbtNodePath, int> GroupReads { get; } = [];
        internal Func<IPbtNodePath, byte[]?>? OverrideNode { get; set; }
        internal Func<IPbtNodePath, RefCountingMemory?>? OverrideGroup { get; set; }
        internal bool ThrowOnApply { get; set; }
        internal Action<IPbtNodePath>? OnApply { get; set; }

        public RefCountingMemory? GetNodeGroup(IPbtNodePath groupKey)
        {
            Reads++;
            GroupReads[groupKey] = GroupReads.GetValueOrDefault(groupKey) + 1;
            if (OverrideGroup is { } overrideGroup)
            {
                RefCountingMemory? overriddenPayload = overrideGroup(groupKey);
                return overriddenPayload;
            }
            if (OverrideNode is { } overrideNode)
            {
                List<PbtNodeRecord> records = [];
                for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
                {
                    if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
                    IPbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
                    byte[]? encoding = overrideNode(path);
                    if (encoding is not null) records.Add(new PbtNodeRecord(path, encoding));
                }

                if (records.Count == 0) return null;
                BufferWriter writer = new(MemoryProvider);
                try
                {
                    PbtNodeGroupCodec.Encode(ref writer, groupKey, records);
                    RefCountingMemory payload = writer.Detach()!;
                    return payload;
                }
                catch
                {
                    writer.Dispose();
                    throw;
                }
            }
            RefCountingMemory? innerPayload = Inner.GetNodeGroup(groupKey);
            return innerPayload;
        }

        public void SetNodeGroup(IPbtNodePath groupKey, RefCountingMemory? payload)
        {
            Applies++;
            OnApply?.Invoke(groupKey);
            LastNodeWrites += Inner.CountNodeChanges(groupKey, payload);
            if (ThrowOnApply) throw new InvalidOperationException("Configured write failure.");
            Inner.SetNodeGroup(groupKey, payload);
        }

        internal void ResetReads()
        {
            Reads = 0;
            LastNodeWrites = 0;
            GroupReads.Clear();
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
