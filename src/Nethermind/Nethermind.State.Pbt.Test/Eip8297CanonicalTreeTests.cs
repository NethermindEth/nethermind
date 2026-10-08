// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
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
        using BulkSerialOracle tree = new();
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
            tree.Apply(changes);
            tree.AssertEquivalentAfterReopen($"round {round}");
        }
    }

    [Test]
    public void Builder_prepares_selected_nibble_and_independent_single_use_outputs()
    {
        using PbtWriteBatchBuilder<PbtStorageTreeKey> builder = new();
        static PbtStorageTreeKey Key(byte nibble, byte suffix) => new([suffix, (byte)(nibble << 4)]);
        PbtStorageTreeKey setKey = Key(15, 1);
        PbtStorageTreeKey deleteKey = Key(2, 2);
        PbtStorageTreeKey zeroKey = Key(8, 3);
        builder.Set(setKey, new ValueHash256(Value(1)));
        builder.SetLeaf(setKey, null);
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
            Assert.That(builder.Count, Is.EqualTo(3));
            Assert.That(table[0], Is.EqualTo((1 << 2) | (1 << 8) | (1 << 15)));
            Assert.That(table.AsSpan().Slice(1, 3).ToArray(), Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(operations, Is.EqualTo(new PbtWriteOperation<PbtStorageTreeKey>[]
            {
                new(deleteKey, default),
                new(zeroKey, default),
                new(setKey, new ValueHash256(Value(2))),
            }));
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
            Assert.That(empty.ConsumeOperations(), Is.Empty);
        }
    }

    [Test]
    public void Batches_release_owned_lists_when_disposed_or_folded([Values] bool? foldFails)
    {
        CountingPbtStore store = new() { ThrowOnApply = foldFails == true };
        using ArrayPoolList<PbtWriteOperation<PbtPath>> operations = new(1);
        operations.Add(new PbtWriteOperation<PbtPath>(new PbtPath(new byte[PbtPath.KeyLength]), new ValueHash256(Value(1))));
        using ArrayPoolList<int> table = new(17, 17);
        table[0] = 1;
        table[1] = 1;
        using PbtWriteBatch<PbtPath> batch = new(operations, table);
        Action update = () => TrieUpdater.UpdateRoot(store, default, new PbtPartitionBatches { Account = batch }, PbtTreeHarness.FoldQuota(), PbtTreeHarness.DefaultFanOut, null);

        if (foldFails is null) batch.Dispose();
        // A single zone never fans out, so the failure surfaces unwrapped.
        else if (foldFails.Value) Assert.Throws<InvalidOperationException>(update);
        else update();

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ObjectDisposedException>(() => operations.AsSpan());
            Assert.Throws<ObjectDisposedException>(() => table.AsSpan());
        }
        AssertAllMemoryReleased(store);
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

    // The root group is rebuilt over its account and storage slots, each folded apart, so a slot that empties, refills or
    // shrinks to a lone leaf beside the other must still settle the root branch over the two.
    [Test]
    public void Root_slots_folded_apart_settle_the_root_branch_over_them([Values("00", "01", "FF")] string loneZone, [Values(1, 3)] int otherCount)
    {
        string otherZone = loneZone == "FF" ? "00" : "FF";
        byte[] lone = ZoneKey(loneZone + "10");
        byte[][] others = [.. Enumerable.Range(0, otherCount).Select(index => ZoneKey($"{otherZone}{index << 4:X2}"))];
        (byte[] Key, byte[]? Value)[][] batches =
        [
            [(lone, Value(1)), .. others.Select(key => (key, (byte[]?)Value(2)))],
            [(lone, null)],
            [(lone, Value(3))],
            [(lone, null)],
            [(lone, Value(3)), (others[0], Value(4))],
            [(lone, Value(5)), .. others.Select(key => (key, (byte[]?)null))],
            [(lone, null)],
        ];
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        for (int batch = 0; batch < batches.Length; batch++)
        {
            tree.ApplyBatch(batches[batch]);
            oracle.Apply(batches[batch]);
            tree.Reopen();
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), $"batch {batch}");
        }
    }

    [Test]
    public void Compressed_boundary_cursor_survives_replacement_collapse_and_reinsertion([Values(11, 15, 16, 71, 72, 171, 255, 271, 527)] int divergenceBit)
    {
        byte[] leftKey = ZoneKey(divergenceBit < PbtPath.KeyLength * 8 ? "00" : "FF");
        byte[] rightKey = (byte[])leftKey.Clone();
        rightKey[divergenceBit >> 3] |= (byte)(0x80 >> (divergenceBit & 7));
        using BulkSerialOracle tree = new();

        tree.Apply([(leftKey, Value(1)), (rightKey, Value(2))]);
        tree.AssertEquivalentAfterReopen("initial boundary");
        tree.Apply([(rightKey, Value(3)), (leftKey, null)]);
        tree.AssertEquivalentAfterReopen("promoted survivor");
        Assert.That(tree.Bulk.Nodes, Has.Count.EqualTo(1));
        tree.Apply([(leftKey, Value(4)), (rightKey, Value(5))]);
        tree.AssertEquivalentAfterReopen("restored boundary");
        tree.Apply([(leftKey, null), (rightKey, null)]);
        tree.AssertEquivalentAfterReopen("emptied");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.Bulk.RootHash, Is.EqualTo(default(ValueHash256)));
            Assert.That(tree.Bulk.Nodes, Is.Empty);
            Assert.That(tree.Bulk.PhysicalPayloads, Is.Empty);
        }
    }

    [Test]
    public void Rewriting_leaves_with_their_current_values_keeps_root_and_records([Range(1, 3)] int leafCount)
    {
        (byte[] Key, byte[]? Value)[] entries = [(AccountKey(0x80, 0x01), Value(1)), (AccountKey(0x40, 0x02), Value(2)), (AccountKey(0x40, 0x03), Value(3))];
        List<(byte[] Key, byte[]? Value)> changes = [.. entries[..leafCount]];
        using BulkSerialOracle tree = new();
        tree.Apply(changes);
        ValueHash256 root = tree.Bulk.RootHash;
        string[] canonical = tree.Bulk.CanonicalRecords();

        tree.Apply(changes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.Bulk.RootHash, Is.EqualTo(root), "identical rewrite root");
            Assert.That(tree.Bulk.CanonicalRecords(), Is.EqualTo(canonical), "identical rewrite records");
        }
        tree.AssertEquivalentAfterReopen("identical rewrite");

        tree.Apply([(entries[0].Key, Value(9))]);
        Assert.That(tree.Bulk.RootHash, Is.Not.EqualTo(root), "changed value root");
        tree.AssertEquivalentAfterReopen("changed value");
    }

    [TestCase(new byte[] { 0x90, 0xC0 }, new byte[] { }, TestName = "Right half folds away under a kept left half")]
    [TestCase(new byte[] { 0x90 }, new byte[] { 0xC0 }, TestName = "First right slot folds away and a later one stays")]
    [TestCase(new byte[] { 0x90, 0xC0 }, new byte[] { 0xE0 }, TestName = "Right half folds away except an insert")]
    [TestCase(new byte[] { 0xC0 }, new byte[] { 0x90 }, TestName = "Nested right quarter folds away under a rewritten left")]
    [TestCase(new byte[] { 0x10, 0x20, 0x90 }, new byte[] { 0xC0 }, TestName = "Left half folds away and the right one stays")]
    [TestCase(new byte[] { 0x10, 0x20, 0x90, 0xC0 }, new byte[] { }, TestName = "Both halves fold away")]
    public void Touched_right_half_folding_away_settles_left_sibling_canonically(byte[] deletedPrefixes, byte[] writtenPrefixes)
    {
        using BulkSerialOracle tree = new();
        List<(byte[] Key, byte[]? Value)> initial = [];
        foreach (byte prefix in new byte[] { 0x10, 0x20, 0x90, 0xC0 }) initial.Add((AccountKey(prefix, 0x00), Value(prefix)));
        tree.Apply(initial);

        List<(byte[] Key, byte[]? Value)> changes = [];
        foreach (byte prefix in deletedPrefixes) changes.Add((AccountKey(prefix, 0x00), null));
        foreach (byte prefix in writtenPrefixes) changes.Add((AccountKey(prefix, 0x00), Value((byte)(prefix + 1))));
        tree.Apply(changes);

        tree.AssertEquivalentAfterReopen("touched right half");
    }

    [TestCase("001200", "001280", "001000")]
    [TestCase("FF1200", "FF1280", "FF1000")]
    [TestCase("00AA00", "00AB00", "00A800")]
    public void Split_inside_compressed_prefix_and_delete_merge_stay_canonical(string firstHex, string secondHex, string splitHex)
    {
        byte[] first = ZoneKey(firstHex);
        byte[] second = ZoneKey(secondHex);
        byte[] split = ZoneKey(splitHex);
        using BulkSerialOracle tree = new();

        tree.Apply([(first, Value(1)), (second, Value(2))]);
        tree.AssertEquivalentAfterReopen("initial split");
        tree.Apply([(split, Value(3)), (second, Value(4))]);
        tree.AssertEquivalentAfterReopen("split inside prefix");
        tree.Apply([(first, null)]);
        tree.AssertEquivalentAfterReopen("promotion and prefix merge");
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
            byte[] expected = EipReferenceTree.Hash([1, (byte)(prefixBits >> 8), (byte)prefixBits, .. prefix,
                .. EipReferenceTree.Hash([0, .. leftKey, .. leftValue]), .. EipReferenceTree.Hash([0, .. rightKey, .. rightValue])]);
            EipReferenceTree oracle = new();
            oracle.Insert(leftKey, leftValue);
            oracle.Insert(rightKey, rightValue);
            Assert.That(oracle.Merkelize(), Is.EqualTo(expected), $"prefix length {prefixBits}");
        }
    }

    [Test]
    public void Inline_paths_preserve_occupied_bytes_and_value_behavior([Values(0, 1, 7, 8, 271, 272, 527, 528)] int bitDepth)
    {
        byte[] source = new byte[PbtStorageTreeKey.MaxLength];
        source.AsSpan().Fill(0xA5);
        PbtStorageTreeKey key = new(source);
        PbtStorageNodePath fromKey = PbtTestPaths.Prefix<PbtStorageNodePath>(key.Bytes, bitDepth);
        byte[] expected = source.AsSpan(0, (bitDepth + 7) >> 3).ToArray();
        if ((bitDepth & 7) != 0) expected[^1] &= (byte)(0xFF << (8 - (bitDepth & 7)));
        byte[] constructorInput = (byte[])expected.Clone();
        PbtStorageNodePath constructed = new(constructorInput, bitDepth);
        constructorInput.AsSpan().Clear();
        source.AsSpan().Clear();
        byte[] copiedPath = constructed.ToPathArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(copiedPath, Is.EqualTo(expected));
            Assert.That(fromKey, Is.EqualTo(constructed));
            if (bitDepth > 0)
            {
                PbtStorageNodePath parent = PbtTestPaths.Prefix<PbtStorageNodePath>(key.Bytes, bitDepth - 1);
                Assert.That(parent.CompareTo(constructed), Is.LessThan(0));
            }
        }
    }

    [Test]
    public void Inline_full_keys_preserve_copies_and_dictionary_identity([Values(1, 34, 66)] int length)
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
            Assert.That(copy.FirstDifferingBit(different, 0), Is.EqualTo(length * 8 - 1));
            Assert.That(copy.FirstDifferingBit(equal, 0), Is.EqualTo(length * 8));
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
    public void First_differing_bit_respects_common_length(
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
            Assert.That(default(CompressedPrefix).MatchingBits(key, keyOffset), Is.Zero);
            Assert.That(CompressedPrefix.FromValidated(encoding).MatchingBits(key, keyOffset), Is.EqualTo(expectedCount));
        }
        for (int differingBit = 0; differingBit < bitCount; differingBit++)
        {
            encoding[sizeof(ushort) + (differingBit >> 3)] ^= (byte)(0x80 >> (differingBit & 7));
            int actual = CompressedPrefix.FromValidated(encoding).MatchingBits(key, keyOffset);
            Assert.That(actual, Is.EqualTo(Math.Min(differingBit, expectedCount)), $"differing bit {differingBit}");
            encoding[sizeof(ushort) + (differingBit >> 3)] ^= (byte)(0x80 >> (differingBit & 7));
        }
    }

#if DEBUG
    [Test]
    public void Invalid_inline_paths_are_rejected()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentException>(() => new PbtStorageNodePath(Bytes.FromHexString("01"), 1));
            Assert.Throws<ArgumentException>(() => new PbtStorageNodePath([], 8));
        }
    }
#endif

    [Test]
    public void Inline_leaves_survive_root_leaf_update_split_and_collapse()
    {
        using PbtTreeHarness tree = new();
        EipReferenceTree oracle = new();
        byte[] first = AccountKey(0x12, 0x34);
        byte[] second = AccountKey(0x12, 0x3C);
        byte[] rootLeaf = PbtTreeHarness.EncodeLeaf(new PbtStorageTreeKey(first));

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
        PbtBranchReader branch = PbtBranchReader.FromValidated(tree.Nodes[0].Encoding.Span);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), "split root leaf");
            Assert.That(tree.Nodes, Has.Count.EqualTo(1));
            Assert.That(branch.LeftKeyPostfix.ToArray(), Is.EqualTo(first));
            Assert.That(branch.RightKeyPostfix.ToArray(), Is.EqualTo(second));
            Assert.That(branch.LeftHash, Is.EqualTo(PbtTreeHarness.HashLeaf(first, Value(2))), "the split reuses the stored leaf hash");
            Assert.That(branch.RightHash, Is.EqualTo(PbtTreeHarness.HashLeaf(second, Value(3))));
        }

        tree.ApplyBatch([(first, null)]);
        oracle.Delete(first);
        tree.Reopen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.RootHash.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()), "collapsed to the remaining leaf");
            Assert.That(tree.Nodes, Has.Count.EqualTo(1));
            Assert.That(tree.Nodes[0].Encoding.ToArray(), Is.EqualTo(PbtTreeHarness.EncodeLeaf(new PbtStorageTreeKey(second))));
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
            ? PbtTreeHarness.EncodeLeaf(new PbtStorageTreeKey(field))
            : PbtTreeHarness.EncodeBranch(field, length, left, right, leftKey, rightKey);
        byte[] backing = new byte[encoding.Length + 11];
        encoding.CopyTo(backing, 7);
        ReadOnlySpan<byte> borrowed = backing.AsSpan(7, encoding.Length);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PbtNodeCodec.IsLeaf(borrowed), Is.EqualTo(leaf));
            Assert.That(encoding, Is.EqualTo(expected));
            if (leaf)
            {
                Assert.That(PbtNodeCodec.LeafKey(borrowed).ToArray(), Is.EqualTo(field));
                Assert.That(PbtNodeCodec.LeafKey(borrowed).Overlaps(backing.AsSpan()), Is.True);
                Assert.That(PbtTreeHarness.HashLeaf(field, left.Bytes).Bytes.ToArray(), Is.EqualTo(EipReferenceTree.Hash([0, .. field, .. left.Bytes])), "leaf hash");
            }
            else
            {
                PbtBranchReader reader = PbtBranchReader.FromValidated(borrowed);
                Assert.That(Blake3Hash.Hash(reader.Preimage).Bytes.ToArray(), Is.EqualTo(EipReferenceTree.Hash(preimage)), "inline leaf keys are not hashed");
                Assert.That(reader.Preimage.ToArray(), Is.EqualTo(preimage));
                Assert.That(reader.Prefix.BitCount, Is.EqualTo(length));
                Assert.That(reader.Prefix.Bytes.ToArray(), Is.EqualTo(field));
                if (length != 0) Assert.That(reader.Prefix.Bytes.Overlaps(backing.AsSpan()), Is.True);
                Assert.That(reader.LeftHash, Is.EqualTo(left));
                Assert.That(reader.RightHash, Is.EqualTo(right));
                Assert.That(reader.LeftKeyPostfix.ToArray(), Is.EqualTo(leftKey));
                Assert.That(reader.RightKeyPostfix.ToArray(), Is.EqualTo(rightKey));
                if (leafChildren != 0) Assert.That((leftKey.Length != 0 ? reader.LeftKeyPostfix : reader.RightKeyPostfix).Overlaps(backing.AsSpan()), Is.True);
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
        CompressedPrefix prefix = CompressedPrefix.FromValidated(encoding);
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

    private static byte[] EncodePrefix(ReadOnlySpan<byte> bytes, int bitCount)
    {
        byte[] encoding = new byte[sizeof(ushort) + bytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(encoding, (ushort)bitCount);
        bytes.CopyTo(encoding.AsSpan(sizeof(ushort)));
        return encoding;
    }

    [TestCaseSource(nameof(MalformedNodeEncodings))]
    public void Node_reader_rejects_malformed_encodings(byte[] encoding) =>
        Assert.Throws<InvalidDataException>(() => PbtNodeCodec.ThrowIfNotExact(encoding));

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
        byte[] leaf = PbtTreeHarness.EncodeLeaf(new PbtStorageTreeKey(Bytes.FromHexString("A0A1")));
        yield return new TestCaseData(leaf[..^1]).SetName("Node_reader_rejects_truncated_leaf");
        yield return new TestCaseData((byte[])[.. leaf, 0]).SetName("Node_reader_rejects_trailing_leaf_bytes");
        byte[] branch = PbtTreeHarness.EncodeBranch(Bytes.FromHexString("A0"), 5, new ValueHash256(Value(1)), new ValueHash256(Value(2)), Bytes.FromHexString("A0"), []);
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
    [Test]
    public void Node_reader_rejects_wrong_kind_access()
    {
        byte[] encoding = PbtTreeHarness.EncodeLeaf(new PbtStorageTreeKey(Bytes.FromHexString("A0")));
        Assert.Throws<InvalidOperationException>(() => _ = PbtBranchReader.FromValidated(encoding));
    }
#endif

    [Test]
    public void Node_reader_construction_and_field_access_do_not_allocate([Values] bool leaf)
    {
        byte[] encoding = leaf
            ? PbtTreeHarness.EncodeLeaf(new PbtStorageTreeKey(Bytes.FromHexString("A0")))
            : PbtTreeHarness.EncodeBranch(Bytes.FromHexString("A0"), 5, new ValueHash256(Value(1)), new ValueHash256(Value(2)), Bytes.FromHexString("A1"), Bytes.FromHexString("A2"));
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
        if (PbtNodeCodec.IsLeaf(encoding)) return encoding.Length + PbtNodeCodec.LeafKey(encoding)[0];
        PbtBranchReader reader = PbtBranchReader.FromValidated(encoding);
        return encoding.Length + reader.Prefix.BitCount + reader.Prefix.Bytes[0] + reader.LeftHash.Bytes[0] + reader.RightHash.Bytes[0] + reader.LeftKeyPostfix[0] + reader.RightKeyPostfix[0];
    }

    [Test]
    public void Current_key_derivation_emits_exact_zone_lengths()
    {
        byte[] address32 = new byte[32];
        address32[0] = 0xA5;
        PbtPath account = Eip8297KeyDerivation.AccountKey(Blake3Hash.Hash(address32), 0);
        PbtStorageTreeKey headerStorage = Eip8297KeyDerivation.StorageKey(address32, new Nethermind.Int256.UInt256(63));
        PbtStorageTreeKey overflowStorage = Eip8297KeyDerivation.StorageKey(address32, new Nethermind.Int256.UInt256(64));
        byte[] expectedAddressHash = EipReferenceTree.Hash(address32);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Length, Is.EqualTo(34));
            Assert.That(account.Bytes.Slice(1, 32).ToArray(), Is.EqualTo(expectedAddressHash));
            Assert.That(account.Bytes[0], Is.EqualTo(0));
            Assert.That(headerStorage.Length, Is.EqualTo(34));
            Assert.That(overflowStorage.Length, Is.EqualTo(66));
            Assert.That(overflowStorage.Bytes[0], Is.EqualTo(0xFF));
        }
    }

    [Test]
    public void Mixed_partition_batches_match_across_fan_outs_and_reopen(
        [Values(0, 1, 2, 3, 4, 16, 17, 31, 32, 33, 256)] int count,
        [Values(1, 16, 256)] int shards)
    {
        using DifferentialTree tree = new(PbtTreeHarness.FanOut(1), PbtTreeHarness.FoldQuota());
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

            tree.Apply(writes);
            using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(tree.ExportPhysicalPayloads());
            Assert.That(reopened.Fold(tree.Root, writes), Is.EqualTo(tree.Root), $"replay after reopen in round {round}");
        }
    }

    [Test]
    public void Single_bucket_prefix_survives_existing_sibling_branches([Values(17, 33)] int count)
    {
        List<(byte[] Key, byte[]? Value)> initial = BoundaryChanges(count, 40, 16, 2);
        initial.Add((ZoneKey("00AA8000000000"), Value(1)));
        initial.Add((ZoneKey("00AAAA80000000"), Value(2)));
        initial.Add((ZoneKey("00AAAAAA800000"), Value(3)));
        List<(byte[] Key, byte[]? Value)> changes = [];
        for (int index = 0; index < count; index++) changes.Add((initial[index].Key, Value(0xEF)));
        using BulkSerialOracle tree = new();
        tree.Apply(initial);
        tree.Apply(changes);
        tree.AssertEquivalentAfterReopen("single bucket with shallower siblings");
        CountingPbtStore store = new();
        ValueHash256 root = store.Fold(default, initial);
        root = store.Fold(root, changes);
        Assert.That(root, Is.EqualTo(tree.Bulk.RootHash));
        AssertAllMemoryReleased(store);
    }

    [Test]
    public void Boundary_bucketization_batches_match_oracle_serial_and_reopen(
        [Values(0, 1, 2, 3, 4, 16, 31, 32, 33, 256)] int count,
        [Values(0, 4, 20)] int groupDepth)
    {
        foreach (int occupiedSlots in new[] { 1, 2, 16 })
        {
            using BulkSerialOracle tree = new();
            List<(byte[] Key, byte[]? Value)> initial = BoundaryChanges(count, groupDepth, occupiedSlots, 0);
            tree.Apply(initial);
            tree.AssertEquivalentAfterReopen("build boundary batch");
            tree.Apply([(ZoneKey("00555555000002"), Value(0x77))]);

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
            tree.Apply(changes);
            tree.AssertEquivalentAfterReopen("mixed boundary batch");

            List<(byte[] Key, byte[]? Value)> deletions = [];
            foreach ((byte[] key, byte[]? _) in initial) deletions.Add((key, null));
            foreach ((byte[] key, byte[]? _) in changes) deletions.Add((key, null));
            tree.Apply(deletions);
            tree.AssertEquivalentAfterReopen("boundary buckets collapse to untouched survivor");
            tree.Apply(initial);
            tree.AssertEquivalentAfterReopen("restore boundary buckets after collapse");
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
    public void All_second_group_boundary_destinations_fetch_only_touched_groups()
    {
        CountingPbtStore store = new();
        (List<(byte[] Key, byte[]? Value)> initial, List<(byte[] Key, byte[]? Value)> changes) = SecondGroupBoundary(Enumerable.Range(0, 16));

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

    [Test]
    public void Dense_group_paths_survive_collapse_and_restoration(
        [Values(8, 12, 248, 252, 520, 524)] int groupDepth,
        [Values(0x0000, 0x0001, 0x8000, 0x000A, 0xA000, 0xA55A, 0x8001, 0xFFFF)] int retainedMask)
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

        using BulkSerialOracle tree = new();
        tree.Apply(initial);
        tree.AssertEquivalentAfterReopen("dense group");
        tree.Apply(deletions);
        tree.AssertEquivalentAfterReopen("collapsed group");
        tree.Apply(initial);
        tree.AssertEquivalentAfterReopen("restored group");
    }

    [Test]
    public void Span_partition_divergence_before_on_and_after_compressed_group_boundaries_matches_oracle(
        [Values(1, 3, 4, 5, 7, 8, 9, 11, 12, 13)] int divergenceBit)
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

        using BulkSerialOracle tree = new();
        tree.Apply([.. initial]);
        tree.Apply([.. changes]);
        tree.AssertEquivalentAfterReopen($"divergence bit {divergenceBit}");
        tree.Apply([(insertedKey, null), (leftKey, Value(5)), (existingRightKey, null)]);
        tree.AssertEquivalentAfterReopen($"collapse divergence bit {divergenceBit}");
        tree.Apply([.. changes]);
        tree.AssertEquivalentAfterReopen($"restore divergence bit {divergenceBit}");
    }

    [Test]
    public void Inline_leaf_keys_omit_group_path_bytes_through_updates_deletes_and_reopen(
        [Values(8, 12, 16, 20, 36, 44)] int sharedBits, [Values(0x00, 0xFF)] byte zone)
    {
        Random random = new(sharedBits * 256 + zone);
        byte[] shared = ZoneKey($"{zone:X2}");
        random.NextBytes(shared.AsSpan(1));
        List<byte[]> keys = [];
        for (int index = 0; index < 24; index++)
        {
            // Every key leaves the shared prefix at one of the next twelve bits, so branches and the leaves they inline
            // land in groups on both sides of the byte boundaries inline keys are cut at.
            byte[] key = ZoneKey($"{zone:X2}");
            random.NextBytes(key.AsSpan(1));
            int divergenceBit = sharedBits + index % 12;
            for (int bit = 8; bit <= divergenceBit; bit++)
            {
                int sharedBit = TrieUpdater.GetBit(shared, bit) ^ (bit == divergenceBit ? 1 : 0);
                key[bit >> 3] = (byte)(key[bit >> 3] & ~(0x80 >> (bit & 7)) | sharedBit << (7 - (bit & 7)));
            }
            keys.Add(key);
        }

        using BulkSerialOracle tree = new();
        HashSet<byte[]> live = new(Bytes.EqualityComparer);
        Apply([.. keys.Select((key, index) => (key, (byte[]?)Value((byte)(index + 1))))], "insert");
        Apply([.. keys.Where((_, index) => index % 2 == 0).Select(key => (key, (byte[]?)Value(0x80)))], "update");
        // Deleting down to a single leaf promotes and lifts branches through every group on the way up.
        while (live.Count > 1)
            Apply([.. live.OrderBy(_ => random.Next()).Take(Math.Max(1, live.Count / 3)).Select(key => (key, (byte[]?)null))], $"delete to {live.Count}");

        void Apply(List<(byte[] Key, byte[]? Value)> changes, string scenario)
        {
            tree.Apply(changes);
            foreach ((byte[] key, byte[]? value) in changes)
            {
                if (value is null) live.Remove(key);
                else live.Add(key);
            }
            tree.AssertEquivalentAfterReopen(scenario);
            using (Assert.EnterMultipleScope())
            {
                foreach (PbtPhysicalPayload payload in tree.Bulk.PhysicalPayloads)
                {
                    int postfixLength = shared.Length - (payload.Key.BitDepth >> 3);
                    foreach ((int _, ReadOnlyMemory<byte> encoding) in PbtStoreTestExtensions.ReadGroup(payload.Key, payload.Payload.Span).Nodes())
                    {
                        if (PbtNodeCodec.IsLeaf(encoding.Span)) continue;
                        PbtBranchReader node = PbtBranchReader.FromValidated(encoding.Span);
                        if (!node.LeftKeyPostfix.IsEmpty) Assert.That(node.LeftKeyPostfix.Length, Is.EqualTo(postfixLength), $"{scenario}: group depth {payload.Key.BitDepth}");
                        if (!node.RightKeyPostfix.IsEmpty) Assert.That(node.RightKeyPostfix.Length, Is.EqualTo(postfixLength), $"{scenario}: group depth {payload.Key.BitDepth}");
                    }
                }
            }
        }
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
    [TestCase("unsorted-ranges")]
    [TestCase("earliest-divergence-in-unsorted-range")]
    [TestCase("interleaved-directions-shared-prefix")]
    [TestCase("first-group-boundary-destinations")]
    [TestCase("second-group-boundary-destinations")]
    [TestCase("descending-inserts")]
    [TestCase("split-inside-long-storage-prefix")]
    [TestCase("sibling-pair-delete")]
    public void Bulk_mutation_kinds_match_oracle_serial_outcome_and_reopen(string scenarioName)
    {
        (List<(byte[] Key, byte[]? Value)> Initial, List<(byte[] Key, byte[]? Value)> Changes) = Scenario(scenarioName);
        using BulkSerialOracle tree = new();
        tree.Apply(Initial);
        tree.Apply(Changes);
        tree.AssertEquivalentAfterReopen(scenarioName);
    }

    [Test]
    public void Split_inside_long_storage_prefix_stores_three_branches_with_inlined_leaves()
    {
        using PbtTreeHarness tree = new();
        tree.ApplyBatch(Scenario("split-inside-long-storage-prefix").Changes);
        Assert.That(tree.Nodes, Has.Count.EqualTo(3));
    }

    [Test]
    public void Mixed_batch_produces_only_the_remaining_trie_leaf([Values] bool deleteKeyExists)
    {
        CountingPbtStore store = new();
        byte[] deleteKeyBytes = AccountKey(0x00);
        byte[] setKeyBytes = AccountKey(0x80);
        ValueHash256 root = deleteKeyExists
            ? store.Fold(default, [(deleteKeyBytes, Value(1))])
            : default;

        ValueHash256 rootAfterUpdate = store.Fold(root, [(deleteKeyBytes, null), (setKeyBytes, Value(2))]);

        byte[] expectedLeaf = PbtTreeHarness.EncodeLeaf(new PbtStorageTreeKey(setKeyBytes));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootAfterUpdate, Is.EqualTo(PbtTreeHarness.HashLeaf(setKeyBytes, Value(2))));
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

        using BulkSerialOracle tree = new();
        tree.Apply(initial);
        tree.Apply(changes);
        tree.AssertEquivalentAfterReopen($"prefix at bit {divergenceBit}, persisted {persisted}");
        Assert.That(root, Is.EqualTo(tree.Bulk.RootHash));
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

    [Test]
    public void Non_empty_update_discovers_persisted_root_instead_of_using_stale_current_root([Values] bool useDefaultRoot)
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
            Assert.That(staleStore.CanonicalRecords(), Is.EqualTo(actualStore.CanonicalRecords()));
            Assert.That(staleStore.PhysicalRecords(), Is.EqualTo(actualStore.PhysicalRecords()));
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

        ValueHash256 changedRoot = store.Fold(root, [(AccountKey(0x00), Value(3)), (AccountKey(0x40), Value(4))]);
        Assert.That(store.Applies, Is.EqualTo(3), "a changed batch touching both leaves publishes the group once");

        ValueHash256 emptiedRoot = store.Fold(changedRoot, [(AccountKey(0x00), null), (AccountKey(0x40), null)]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Applies, Is.EqualTo(4), "deleting both leaves publishes the group once");
            Assert.That(emptiedRoot, Is.EqualTo(default(ValueHash256)));
            Assert.That(store.Inner.EnumerateNodeGroupKeys(), Is.Empty);
        }

        AssertAllMemoryReleased(store);
    }

    // Each case leaves the group at 0x0000 with a boundary node that owns nothing below it, so the fold that
    // enters it must publish it without ever reading it.
    [TestCase(new byte[] { 0x80 }, new byte[] { 0x00, 0x08 }, TestName = "Absent_group_is_never_fetched_over_an_empty_boundary")]
    [TestCase(new byte[] { 0x80, 0x00 }, new byte[] { 0x04 }, TestName = "Absent_group_is_never_fetched_over_a_leaf_boundary")]
    [TestCase(new byte[] { 0x80, 0x00, 0x08 }, new byte[] { 0x00 }, TestName = "Absent_group_is_never_fetched_over_two_inlined_leaves")]
    [TestCase(new byte[] { 0x00, 0x08, 0x80, 0x88 }, new byte[] { 0x00 }, TestName = "Absent_group_is_never_fetched_beside_an_untouched_sibling_group")]
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
            Assert.That(store.GroupReads.Values, Has.All.EqualTo(1));
            Assert.That(store.GroupReads.Keys.Where(key => key.BitDepth > 8), Is.Empty);
            Assert.That(store.Inner.PhysicalRecords(), Is.EqualTo(expected.PhysicalPayloads.PhysicalRecords()));
            PbtStoreTestExtensions.AssertSubtreeBytes(store.Inner.ExportPhysicalPayloads());
        }

        static (byte[] Key, byte[]? Value)[] ToChanges(byte[] keys, byte marker)
        {
            (byte[] Key, byte[]? Value)[] changes = new (byte[], byte[]?)[keys.Length];
            for (int index = 0; index < keys.Length; index++) changes[index] = (AccountKey(keys[index]), Value((byte)(keys[index] + marker)));
            return changes;
        }
    }

    public enum FoldFailure { MissingNode, EmptyGroup, MalformedGroup, ApplyThrows }

    [TestCase(FoldFailure.MissingNode, typeof(InvalidDataException), 0)]
    [TestCase(FoldFailure.EmptyGroup, typeof(Exception), 0)]
    [TestCase(FoldFailure.MalformedGroup, typeof(Exception), 0)]
    [TestCase(FoldFailure.ApplyThrows, typeof(InvalidOperationException), 1)]
    public void Failed_fold_releases_leases_and_leaves_state_unchanged(FoldFailure failure, Type expectedException, int appliesDelta)
    {
        CountingPbtStore store = new();
        // [0x13] keeps a stored branch below the root for the override to hide.
        ValueHash256 root = store.Fold(default, [(AccountKey(0x12), Value(1)), (AccountKey(0x92), Value(2)), (AccountKey(0x13), Value(4))]);
        PbtPhysicalPayload[] before = [.. store.Inner.ExportPhysicalPayloads()];
        int appliesBeforeFailure = store.Applies;
        switch (failure)
        {
            case FoldFailure.MissingNode:
                store.OverrideNode = path => path.BitDepth == 0 ? store.Inner.GetNode(path) : null;
                break;
            case FoldFailure.ApplyThrows:
                store.ThrowOnApply = true;
                break;
            default:
                store.OverrideGroup = (_, _) =>
                {
                    RefCountingMemory memory = store.MemoryProvider.Rent(failure == FoldFailure.MalformedGroup ? 1 : 0);
                    if (failure == FoldFailure.MalformedGroup) memory.GetSpan()[0] = 0x01;
                    return memory;
                };
                break;
        }

        TrackingMemoryProvider memoryProvider = new();
        Assert.Catch(expectedException, () => store.Fold(root, [(AccountKey(0x12), Value(3))], PbtTreeHarness.DefaultFanOut, memoryProvider));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Applies, Is.EqualTo(appliesBeforeFailure + appliesDelta));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
            Assert.That(store.Inner.PhysicalRecords(), Is.EqualTo(before.PhysicalRecords()));
        }

        AssertAllMemoryReleased(store);
    }

    [Test]
    public void Group_outputs_retain_only_store_leases_after_mutations_and_partition_folds([Values] bool parallel)
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
            oracle.Apply(changes);
            using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(store.ExportPhysicalPayloads());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root, Is.EqualTo(expectedRoot));
                Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
                Assert.That(reopened.PhysicalRecords(), Is.EqualTo(expectedStore.PhysicalRecords()));
                AssertOnlyPublishedRentalsRemain(store, memoryProvider);
            }
        }
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        Assert.That(memoryProvider.RentCount, Is.GreaterThan(0));
    }

    [Test]
    public void Owned_node_encodings_are_released_at_every_rent_failure([Values] bool parallel)
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

    [Test]
    public void Owned_node_encodings_are_released_when_worker_or_ancestor_publish_fails([Values] bool parallel, [Values(0, 4, 8)] int failedDepth)
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
        root = store.Fold(root, [(ZoneKey("00123458"), null)], PbtTreeHarness.DefaultFanOut, nodeProvider);
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
        // The pairs below the root omit the zone byte of their group's path from their inline keys.
        int inlineKeyLength = singlePair ? PbtPath.KeyLength : PbtPath.KeyLength - 1;
        int pairBranchLength = PbtNodeCodec.BranchLength(singlePair ? 8 : 0, inlineKeyLength, inlineKeyLength);
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
        List<(int Position, ReadOnlyMemory<byte> Encoding)> nodes = PbtStoreTestExtensions.ReadGroup(groupKey, group.Payload.Span).Nodes();
        List<PbtNodeRecord> records = [];
        foreach ((int position, ReadOnlyMemory<byte> encoding) in nodes)
            records.Add(new(PbtTestPaths.PathOf(groupKey, position), encoding.Span));
        byte[] expectedPayload = PbtNodeGroupEncoder.Encode(groupKey, records, default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(nodes, Has.Count.EqualTo(storedNodes));
            Assert.That(group.Payload.Length, Is.EqualTo(PbtNodeGroupCodec.HeaderLength + storedNodes * pairBranchLength
                + storedNodes * sizeof(ushort) + sizeof(uint) + PbtNodeGroupCodec.DescendantMaskLength));
            Assert.That(group.Payload.ToArray(), Is.EqualTo(expectedPayload));
            Assert.That(provider.RequestedLengths, Is.EquivalentTo(payloads.Select(static payload => payload.Payload.Length)),
                "every published group is rented once, at its final size");
            AssertOnlyPublishedRentalsRemain(store, provider);
        }
        using PbtNodeGroupStore reopened = PbtNodeGroupStore.FromPhysicalPayloads(payloads);
        Assert.That(reopened.PhysicalRecords(), Is.EqualTo(payloads.PhysicalRecords()));
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
            PbtBranchReader branch = PbtBranchReader.FromValidated(PbtStoreTestExtensions.ReadGroup(branchGroupKey, original!.GetSpan()).GetEncoding(18).Span);
            Assert.That(branch.Prefix.BitCount, Is.EqualTo(prefixBits - 12));
        }
        root = store.Fold(root, [(sibling, null)]);
        EipReferenceTree oracle = new();
        oracle.Insert(left, Value(1));
        oracle.Insert(right, Value(2));
        using RefCountingMemory? updated = store.GetPhysicalNodeGroup(groupKey);
        GroupFrameReader<PbtStorageTreeKey, PbtStorageNodePath> updatedReader = PbtStoreTestExtensions.ReadGroup(groupKey, updated!.GetSpan());
        PbtBranchReader promoted = PbtBranchReader.FromValidated(updatedReader.GetEncoding(30).Span);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.Bytes.ToArray(), Is.EqualTo(oracle.Merkelize()));
            Assert.That(updatedReader.Nodes(), Has.Count.EqualTo(1));
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
        store.Fold(root, changes, parallel ? PbtTreeHarness.FanOut(1) : PbtTreeHarness.DefaultFanOut, memoryProvider);

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
        "unsorted-ranges" => ([(AccountKey(0x10), Value(1)), (AccountKey(0x20), Value(2))],
            [(AccountKey(0x12), Value(3)), (AccountKey(0x80), Value(4)), (AccountKey(0x1F), Value(5)), (AccountKey(0x21), Value(6)),
             (AccountKey(0x02), Value(7)), (AccountKey(0x40), Value(8))]),
        "earliest-divergence-in-unsorted-range" => ([], [(AccountKey(0x00), Value(1)), (AccountKey(0x80), Value(2)), (AccountKey(0x40), Value(3)), (AccountKey(0x01), Value(4))]),
        "interleaved-directions-shared-prefix" => ([],
            [(AccountKey(0x12, 0x00), Value(1)), (AccountKey(0x13, 0x80), Value(2)), (AccountKey(0x12, 0x80), Value(3)), (AccountKey(0x13, 0x00), Value(4))]),
        "first-group-boundary-destinations" => ([], FirstGroupBoundaryChanges()),
        "second-group-boundary-destinations" => SecondGroupBoundary([13, 1, 8, 3, 15, 0, 6, 11, 4, 14, 2, 10, 7, 5, 12, 9]),
        "descending-inserts" => ([],
            [(AccountKey(0x80), Value(1)), (AccountKey(0x40), Value(2)), (AccountKey(0x20), Value(3)),
             (AccountKey(0x10), Value(4)), (AccountKey(0x08), Value(5)), (AccountKey(0x04), Value(6))]),
        "split-inside-long-storage-prefix" => ([],
            [(ZoneKey("FF1200"), Value(1)), (ZoneKey("FF1280"), Value(2)), (ZoneKey("FF1000"), Value(3)), (ZoneKey("FF1240"), Value(4))]),
        "sibling-pair-delete" => (
            [(AccountKey(0x00), Value(1)), (AccountKey(0x40), Value(2)), (AccountKey(0x41, 0x80), Value(3)),
             (AccountKey(0xFF, 0x10), Value(4)), (AccountKey(0x12, 0x34, 0x56, 0x78), Value(5))],
            [(AccountKey(0x40), new byte[32]), (AccountKey(0x41, 0x80), null)]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static List<(byte[] Key, byte[]? Value)> FirstGroupBoundaryChanges()
    {
        int[] order = [7, 15, 2, 10, 0, 12, 5, 1, 9, 14, 3, 8, 6, 11, 4, 13];
        List<(byte[] Key, byte[]? Value)> changes = [];
        foreach (int destination in order)
            changes.Add((AccountKey((byte)(destination << 4), (byte)(0x20 + destination)), Value((byte)(destination + 1))));
        return changes;
    }

    private static (List<(byte[] Key, byte[]? Value)> Initial, List<(byte[] Key, byte[]? Value)> Changes) SecondGroupBoundary(IEnumerable<int> changeOrder)
    {
        List<(byte[] Key, byte[]? Value)> initial = [];
        for (int destination = 0; destination < 16; destination++)
            initial.Add((AccountKey((byte)(0xA0 | destination), 0x11), Value((byte)(destination + 1))));
        initial.Add((AccountKey(0xB1, 0x11), Value(0xEE)));
        List<(byte[] Key, byte[]? Value)> changes = [];
        foreach (int destination in changeOrder)
            changes.Add((AccountKey((byte)(0xA0 | destination), 0x11), Value((byte)(0x40 + destination))));
        return (initial, changes);
    }

    /// <summary>A tree folded in bulk, a tree folded one change at a time and the reference tree, fed the same changes.</summary>
    private sealed class BulkSerialOracle : IDisposable
    {
        private readonly PbtTreeHarness _serial = new();
        private readonly EipReferenceTree _oracle = new();

        public PbtTreeHarness Bulk { get; } = new();

        public void Apply(List<(byte[] Key, byte[]? Value)> changes)
        {
            Bulk.ApplyBatch(changes);
            foreach ((byte[] key, byte[]? value) in changes)
                _serial.ApplyBatch([(key, value)]);
            _oracle.Apply(changes);
        }

        public void AssertEquivalentAfterReopen(string scenario)
        {
            string[] canonical = Bulk.CanonicalRecords();
            string[] physical = Bulk.PhysicalPayloads.PhysicalRecords();
            PbtStoreTestExtensions.AssertSubtreeBytes(Bulk.PhysicalPayloads);
            Bulk.Reopen();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Bulk.RootHash.Bytes.ToArray(), Is.EqualTo(_oracle.Merkelize()), scenario);
                Assert.That(Bulk.RootHash, Is.EqualTo(_serial.RootHash), "bulk and serial roots");
                Assert.That(Bulk.CanonicalRecords(), Is.EqualTo(_serial.CanonicalRecords()), "bulk and serial canonical records");
                Assert.That(Bulk.PhysicalPayloads.PhysicalRecords(), Is.EqualTo(_serial.PhysicalPayloads.PhysicalRecords()), "bulk and serial physical records");
                Assert.That(Bulk.CanonicalRecords(), Is.EqualTo(canonical), "canonical records survive reopen");
                Assert.That(Bulk.PhysicalPayloads.PhysicalRecords(), Is.EqualTo(physical), "physical groups survive reopen");
            }
        }

        public void Dispose()
        {
            Bulk.Dispose();
            _serial.Dispose();
        }
    }

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
                for (int position = 0; position < PbtFourLevelGroupGeometry.PositionCount; position++)
                {
                    if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
                    PbtStorageNodePath path = PbtTestPaths.PathOf(storageGroupKey, position);
                    byte[]? encoding = overrideNode(path);
                    if (encoding is not null) records.Add(new PbtNodeRecord(path, encoding));
                }

                if (records.Count == 0) return null;
                return PbtNodeGroupEncoder.EncodeToMemory(storageGroupKey, records, MemoryProvider);
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

    /// <summary>An account-zone key whose bytes after the zone byte start with <paramref name="suffix"/>.</summary>
    private static byte[] AccountKey(params ReadOnlySpan<byte> suffix)
    {
        byte[] key = new byte[PbtPath.KeyLength];
        suffix.CopyTo(key.AsSpan(1));
        return key;
    }
}
