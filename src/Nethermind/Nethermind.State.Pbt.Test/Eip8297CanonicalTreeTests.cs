// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class Eip8297CanonicalTreeTests
{
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
        byte[] keyBytes = new byte[PbtFullKey.MaxLength];
        new Random(8297).NextBytes(keyBytes);
        PbtFullKey key = new(keyBytes);
        int pathDepth = 8 + pathOffset;
        PbtNodePath path = PbtNodePath.FromKey(key, pathDepth);
        PbtBitPrefix prefix = PbtBitPrefix.FromKey(key, pathOffset, prefixLength);
        byte[] expectedPrefix = new byte[(prefixLength + 7) >> 3];
        CopyBitsReference(keyBytes, pathOffset, prefixLength, expectedPrefix, 0);
        int resultDepth = pathDepth + prefixLength + 1;
        byte[] expectedPath = new byte[(resultDepth + 7) >> 3];
        CopyBitsReference(keyBytes, 0, pathDepth, expectedPath, 0);
        CopyBitsReference(expectedPrefix, 0, prefixLength, expectedPath, pathDepth);
        expectedPath[(resultDepth - 1) >> 3] |= (byte)(direction << (7 - ((resultDepth - 1) & 7)));

        PbtNodePath appended = path.Append(prefix, direction);
        PbtBitPrefix concatenated = PbtBitPrefix.Concat(new PbtBitPrefix(path.Path, pathDepth), direction, prefix);
        byte[] expectedConcat = new byte[expectedPath.Length];
        CopyBitsReference(keyBytes, 0, pathDepth, expectedConcat, 0);
        expectedConcat[pathDepth >> 3] |= (byte)(direction << (7 - (pathDepth & 7)));
        CopyBitsReference(expectedPrefix, 0, prefixLength, expectedConcat, pathDepth + 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(prefix.Bytes.ToArray(), Is.EqualTo(expectedPrefix));
            Assert.That(appended, Is.EqualTo(new PbtNodePath(expectedPath, resultDepth)));
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

    [TestCase(1)]
    [TestCase(32)]
    [TestCase(PbtFullKey.MaxLength)]
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
        byte[] source = new byte[PbtFullKey.MaxLength];
        source.AsSpan().Fill(0xA5);
        PbtFullKey key = new(source);
        PbtNodePath fromKey = PbtNodePath.FromKey(key, bitDepth);
        byte[] expected = source.AsSpan(0, (bitDepth + 7) >> 3).ToArray();
        if ((bitDepth & 7) != 0) expected[^1] &= (byte)(0xFF << (8 - (bitDepth & 7)));
        byte[] constructorInput = (byte[])expected.Clone();
        PbtNodePath constructed = new(constructorInput, bitDepth);
        constructorInput.AsSpan().Clear();
        source.AsSpan().Clear();
        PbtNodePath decoded = PbtNodePath.Decode(constructed.Encode());
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
                PbtNodePath parent = PbtNodePath.FromKey(key, bitDepth - 1);
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
        PbtFullKey key = new(source);
        PbtFullKey copy = key;
        source[^1] ^= 1;
        PbtFullKey different = new(source);
        source[^1] ^= 1;
        PbtFullKey equal = new(source);
        Dictionary<PbtFullKey, int> keys = new() { [key] = 42 };
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
            Assert.That(key.GetHashCode(), Is.EqualTo(default(PbtFullKey).GetHashCode()));
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
            Assert.Throws<ArgumentOutOfRangeException>(() => new PbtFullKey(new byte[length]));
            Assert.Throws<InvalidDataException>(() => PbtNodeCodec.Decode(encoding));
        }
    }

    [Test]
    public void Invalid_inline_paths_and_default_complete_keys_reject_before_mutation()
    {
        PbtNodePath maximum = new(new byte[66], 528);
        PbtWriteBatch batch = new();
        using PbtSnapshotContent content = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PbtNodePath(new byte[67], 529));
            Assert.Throws<InvalidDataException>(() => PbtNodePath.Decode([0, 0, 2, 17, .. new byte[67]]));
            Assert.Throws<ArgumentOutOfRangeException>(() => maximum.Append(new PbtBitPrefix([], 0), 0));
            Assert.Throws<ArgumentException>(() => new PbtNodePath(Bytes.FromHexString("01"), 1));
            Assert.Throws<ArgumentException>(() => new PbtNodePath([], 8));
            Assert.Throws<ArgumentException>(() => batch.Set(default, default));
            Assert.Throws<ArgumentException>(() => batch.Delete(default));
            Assert.Throws<ArgumentException>(() => content.SetLeaf(default, default));
            Assert.Throws<ArgumentException>(() => new PbtLeafNode(default, default(ValueHash256)));
            Assert.That(batch.Count, Is.Zero);
            Assert.That(content.Leaves, Is.Empty);
        }
    }

    [Test]
    public void Full_key_and_persisted_path_validate_bounds_and_canonical_padding()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtFullKey([]));
        Assert.DoesNotThrow(() => new PbtFullKey(new byte[PbtFullKey.MaxLength]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PbtFullKey(new byte[PbtFullKey.MaxLength + 1]));
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

    [TestCase(1, 188)]
    [TestCase(34, 189)]
    [TestCase(66, 190)]
    public void Node_hashes_match_independent_preimages_at_stack_threshold(int keyLength, int prefixLength)
    {
        byte[] key = new byte[keyLength];
        key[^1] = 1;
        byte[] value = Value(7);
        PbtLeafNode leaf = new(new PbtFullKey(key), value);

        int prefixBitCount = prefixLength * 8;
        byte[] prefixBytes = new byte[prefixLength];
        PbtBitPrefix prefix = new(prefixBytes, prefixBitCount);
        ValueHash256 left = new(Hash([0, .. key, .. value]));
        ValueHash256 right = new(Value(8));
        PbtBranchNode branch = new(prefix, left, right);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(leaf.Hash.Bytes.ToArray(), Is.EqualTo(Hash([0, .. key, .. value])), "leaf");
            Assert.That(branch.Hash.Bytes.ToArray(), Is.EqualTo(Hash(
                [1, (byte)(prefixBitCount >> 8), (byte)prefixBitCount, .. prefixBytes, .. left.Bytes, .. right.Bytes])), "branch");
        }
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
        PbtWriteOperation[] operations = new PbtWriteOperation[count];
        for (int index = 0; index < count; index++)
        {
            PbtFullKey key = new(changes[index].Key);
            operations[index] = index % 3 == 0
                ? PbtWriteOperation.Delete(key)
                : PbtWriteOperation.Set(key, new ValueHash256(changes[index].Value!));
        }
        PbtWriteOperation[] original = (PbtWriteOperation[])operations.Clone();

        int[] offsets = new int[PbtFourLevelGroupGeometry.BoundarySlots + 1];
        Array.Fill(offsets, -1);
        TrieUpdater.PartitionOutcome partition = TrieUpdater.BucketizeByGroupBoundary(operations, groupDepth, offsets);
        int expectedMask = 0;
        int expectedBranchDepth = count == 0 ? groupDepth : original[0].Key.BitLength;
        for (int index = 0; index < count; index++)
        {
            PbtFullKey key = original[index].Key;
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
            Assert.That(partition.BranchDepth, Is.EqualTo(expectedBranchDepth));
            Assert.That(destinations, Is.Ordered);
            Assert.That(operations, Is.EquivalentTo(original));
            Assert.That(offsets[0], Is.Zero);
            Assert.That(offsets[^1], Is.EqualTo(count));
            Assert.That(offsets, Is.Ordered);
            int expectedOffset = 0;
            for (int bucket = 0; bucket < PbtFourLevelGroupGeometry.BoundarySlots; bucket++)
            {
                Assert.That(offsets[bucket], Is.EqualTo(expectedOffset), $"bucket {bucket} start");
                expectedOffset += bucketCounts[bucket];
                Assert.That(offsets[bucket + 1], Is.EqualTo(expectedOffset), $"bucket {bucket} end");
            }
        }
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
            Assert.That(store.GroupReads.ContainsKey(untouchedGroup), Is.False);
            Assert.That(metrics.PhysicalGroupFetches, Is.EqualTo(store.Reads));
            Assert.That(metrics.GroupParses, Is.EqualTo(store.Reads));
            Assert.That(metrics.EmittedNodeWrites, Is.EqualTo(store.LastNodeWrites));
        }
    }

    [TestCase(0)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(12)]
    [TestCase(248)]
    [TestCase(252)]
    [TestCase(520)]
    [TestCase(524)]
    public void Dense_group_paths_survive_collapse_and_restoration(int groupDepth)
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
            if (slot != 0 && slot != 15) deletions.Add((key, null));
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

        PbtLeafNode expectedLeaf = new(new PbtFullKey(setKeyBytes), Value(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootAfterUpdate, Is.EqualTo(expectedLeaf.Hash));
            Assert.That(store.Inner.EnumerateRecords(), Has.Count.EqualTo(1));
            Assert.That(store.GetNode(new PbtNodePath([], 0)), Is.EqualTo(PbtNodeCodec.Encode(expectedLeaf)));
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
            Assert.That(metrics.OperationPrefixComparisons, Is.EqualTo(1));
            Assert.That(metrics.SynthesizedSingleBuckets, Is.EqualTo(persisted && divergenceBit >= 4 ? 1 : 0));
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
        ValueHash256 result = TrieUpdater.UpdateRoot(store, suppliedRoot, new PbtWriteBatch());

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
        PbtWriteBatch changes = Batch(([0x12], Value(4)), ([0xA0], Value(5)));

        using PbtNodeGroupStore staleStore = PbtNodeGroupStore.FromPhysicalPayloads(initialPayloads);
        using PbtNodeGroupStore actualStore = PbtNodeGroupStore.FromPhysicalPayloads(initialPayloads);
        ValueHash256 resultFromStaleRoot = TrieUpdater.UpdateRoot(staleStore, staleRoot, changes);
        ValueHash256 resultFromActualRoot = TrieUpdater.UpdateRoot(actualStore, actualRoot, changes);

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
        PbtWriteBatch initial = Batch(([0x00], Value(1)), ([0x40], Value(2)));
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, initial);
        store.ResetReads();
        TrieUpdaterMetrics metrics = new();

        ValueHash256 unchangedRoot = TrieUpdater.UpdateRoot(store, root, initial, metrics);

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

        PbtNodePath untouchedGroup = new([0x80], 4);
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

        Action update = () => TrieUpdater.UpdateRoot(store, root, Batch(([0x12], Value(2))));
        if (applyFailure) Assert.Throws<InvalidOperationException>(update);
        else Assert.Throws<InvalidDataException>(update);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Applies, Is.EqualTo(appliesBeforeFailure + (applyFailure ? 1 : 0)));
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

        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, root, Batch(([0x12], Value(3)))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Applies, Is.EqualTo(appliesBeforeFailure));
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
        internal Dictionary<PbtNodePath, int> GroupReads { get; } = [];
        internal Func<PbtNodePath, byte[]?>? OverrideNode { get; set; }
        internal Func<PbtNodePath, RefCountingMemory?>? OverrideGroup { get; set; }
        internal bool ThrowOnApply { get; set; }

        public RefCountingMemory? GetNodeGroup(PbtNodePath groupKey)
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
                    PbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
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

        public void SetNodeGroup(PbtNodePath groupKey, RefCountingMemory? payload)
        {
            Applies++;
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
