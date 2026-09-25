// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>Checks <see cref="TrieUpdater{TKey,TPath}.UpdateRootSorted"/> against the bucketing updater, group for group.</summary>
public class SortedTrieUpdaterTests
{
    public enum KeyKind { Variable, Fixed }

    [Test]
    public void Random_batches_match_bucketing_updater(
        [Values] KeyKind keyKind,
        [Values] PbtPrefixlessBranchOmission omission,
        [Values(1, 2, 3)] int seed)
    {
        if (keyKind == KeyKind.Variable) RunRandom<PbtStorageTreeKey, PbtStorageNodePath>(omission, seed, VariableKey);
        else RunRandom<PbtPath, PbtNodePath>(omission, seed, static random => RandomBytes(random, PbtPath.KeyLength));
    }

    /// <summary>Batches of writes, each <c>key=value</c> or a bare key to delete.</summary>
    private static IEnumerable<TestCaseData> Shapes()
    {
        yield return Shape("Empty_to_one_to_two_leaves_and_back",
            ["0102=11"],
            ["0103=22"],
            ["0102", "0103"]);
        yield return Shape("Terminal_key_replaced_by_longer_keys_and_back",
            ["AB=11", "12=22"],
            ["AB", "ABCD=33"],
            ["ABCD", "AB01=44", "AB02=55"],
            ["AB01", "AB02", "AB=66"]);
        yield return Shape("Branch_spanning_absent_groups_split_and_raised",
            ["00000000000001=11", "00000000000002=22"],
            ["08000000000000=33"],
            ["00000000000001"],
            ["08000000000000", "00000000000003=44"],
            ["00000000000002", "00000000000003"]);
        yield return Shape("Deletions_emptying_right_half_raise_left",
            ["1000=11", "1100=22", "1800=33", "1F00=44", "2000=55"],
            ["1000", "1100", "1800=66"],
            ["1800", "1F00"]);

        static TestCaseData Shape(string name, params string[][] batches) => new TestCaseData((object)batches).SetName(name);
    }

    [TestCaseSource(nameof(Shapes))]
    public void Targeted_shapes_match_bucketing_updater(string[][] batches)
    {
        foreach (PbtPrefixlessBranchOmission omission in Enum.GetValues<PbtPrefixlessBranchOmission>())
        {
            using DifferentialTree<PbtStorageTreeKey, PbtStorageNodePath> tree = new(omission);
            foreach (string[] batch in batches)
                tree.Apply([.. batch.Select(static write => write.Split('=') is [string key, string value]
                    ? (Bytes.FromHexString(key), Value(Bytes.FromHexString(value)[0]))
                    : (Bytes.FromHexString(write), (byte[]?)null))]);
        }
    }

    [Test]
    public void Prefix_violation_throws_like_bucketing_updater()
    {
        using PbtNodeGroupStore store = new();
        PbtWriteOperation<PbtStorageTreeKey>[] operations =
        [
            new(new PbtStorageTreeKey(Bytes.FromHexString("AB")), new ValueHash256(Value(1))),
            new(new PbtStorageTreeKey(Bytes.FromHexString("ABCD")), new ValueHash256(Value(2))),
        ];
        Assert.That(() => TrieUpdater<PbtStorageTreeKey, PbtStorageNodePath>.UpdateRootSorted(store, default, operations, PbtPrefixlessBranchOmission.Interior),
            Throws.ArgumentException.With.Message.Contains("prefix-free"));
    }

    [Test]
    public void Partitioned_sorted_zone_fold_matches_bucketing([Values] PbtPrefixlessBranchOmission omission, [Values(1, 2, 3)] int seed)
    {
        using PbtNodeGroupStore bucketingStore = new();
        using PbtNodeGroupStore sortedStore = new();
        ValueHash256 bucketingRoot = default;
        ValueHash256 sortedRoot = default;
        foreach ((byte[] Key, byte[]? Value)[] writes in RandomRounds(new Random(seed), ZoneKey))
        {
            // A single-operation minimum splits every zone and frame, so shards sort and slots fold across threads even for small batches.
            using (PbtPartitionBatches changes = PbtStoreTestExtensions.PreparePartitions(writes))
                bucketingRoot = TrieUpdater.UpdateRoot(bucketingStore, bucketingRoot, changes, PbtTreeHarness.FoldQuota(), PbtTreeHarness.FanOut(1), omission, false, null);
            using (PbtPartitionBatches changes = PbtStoreTestExtensions.PreparePartitions(writes))
                sortedRoot = TrieUpdater.UpdateRoot(sortedStore, sortedRoot, changes, PbtTreeHarness.FoldQuota(), PbtTreeHarness.FanOut(1), omission, true, null);

            IReadOnlyList<PbtPhysicalPayload> actual = sortedStore.ExportPhysicalPayloads();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sortedRoot, Is.EqualTo(bucketingRoot));
                Assert.That(actual.Select(Describe), Is.EqualTo(bucketingStore.ExportPhysicalPayloads().Select(Describe)));
            }
            PbtStoreTestExtensions.AssertSubtreeBytes(actual);
        }
    }

    private static void RunRandom<TKey, TPath>(PbtPrefixlessBranchOmission omission, int seed, Func<Random, byte[]> newKey)
        where TKey : unmanaged, IPbtKey<TKey>
        where TPath : struct, IPbtNodePath<TPath>
    {
        using DifferentialTree<TKey, TPath> tree = new(omission);
        foreach ((byte[] Key, byte[]? Value)[] writes in RandomRounds(new Random(seed), newKey)) tree.Apply(writes);
    }

    /// <summary>Rounds of distinct writes mixing inserts, updates, clustered keys and deletes, with every sixth round deleting everything.</summary>
    private static IEnumerable<(byte[] Key, byte[]? Value)[]> RandomRounds(Random random, Func<Random, byte[]> newKey)
    {
        List<byte[]> live = [];
        HashSet<string> liveSet = [];
        for (int round = 0; round < 24; round++)
        {
            int batchSize = round % 6 == 5 ? live.Count + 1 : 1 + random.Next(round < 4 ? 600 : 80);
            bool deleteEverything = round % 6 == 5;
            Dictionary<string, (byte[] Key, byte[]? Value)> writes = [];
            for (int index = 0; index < batchSize; index++)
            {
                byte[] key;
                int choice = random.Next(10);
                if (deleteEverything)
                {
                    if (index >= live.Count) break;
                    key = live[index];
                }
                else if (choice < 4 && live.Count > 0) key = live[random.Next(live.Count)];
                else if (choice < 7 && live.Count > 0) key = Clustered(random, live[random.Next(live.Count)]);
                else key = newKey(random);

                bool delete = deleteEverything || (choice is 0 or 1 && live.Count > 0) || random.Next(20) == 0;
                writes[key.ToHexString()] = (key, delete ? null : Value((byte)random.Next(1, 4)));
            }

            yield return [.. writes.Values];
            foreach ((byte[] key, byte[]? value) in writes.Values)
            {
                string hex = key.ToHexString();
                if (value is null)
                {
                    if (liveSet.Remove(hex)) live.RemoveAll(existing => existing.AsSpan().SequenceEqual(key));
                }
                else if (liveSet.Add(hex))
                {
                    live.Add(key);
                }
            }
        }
    }

    /// <summary>A key in the account, code or storage zone, at that zone's key length.</summary>
    private static byte[] ZoneKey(Random random)
    {
        byte zone = random.Next(3) switch
        {
            0 => Eip8297KeyDerivation.AccountZone,
            1 => Eip8297KeyDerivation.CodeZone,
            _ => Eip8297KeyDerivation.StorageZone,
        };
        byte[] key = RandomBytes(random, zone == Eip8297KeyDerivation.StorageZone ? PbtStoragePath.KeyLength : PbtPath.KeyLength);
        key[0] = zone;
        return key;
    }

    /// <summary>A key sharing a random-length prefix with <paramref name="existing"/>, so branches span groups and split deep.</summary>
    private static byte[] Clustered(Random random, byte[] existing)
    {
        byte[] key = (byte[])existing.Clone();
        int bit = 8 + random.Next(key.Length * 8 - 8);
        key[bit >> 3] ^= (byte)(0x80 >> (bit & 7));
        for (int index = (bit >> 3) + 1; index < key.Length; index++) key[index] = (byte)random.Next(256);
        return key;
    }

    /// <summary>A key whose first byte is its length, so keys of different lengths never prefix one another.</summary>
    private static byte[] VariableKey(Random random)
    {
        int length = 1 + random.Next(8);
        byte[] key = RandomBytes(random, length + 1);
        key[0] = (byte)length;
        return key;
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static string Describe(PbtPhysicalPayload payload) =>
        $"{payload.Key.ToEncodedArray().ToHexString()}:{payload.Payload.Span.ToHexString()}";

    private static byte[] Value(byte seed)
    {
        byte[] value = new byte[32];
        value[31] = seed;
        return value;
    }

    /// <summary>Applies every batch through both updaters, the sorted one serially and in parallel, each on its own store, and asserts identical roots and groups.</summary>
    private sealed class DifferentialTree<TKey, TPath>(PbtPrefixlessBranchOmission omission) : IDisposable
        where TKey : unmanaged, IPbtKey<TKey>
        where TPath : struct, IPbtNodePath<TPath>
    {
        private readonly PbtNodeGroupStore _bucketingStore = new();
        private readonly PbtNodeGroupStore _sortedStore = new();
        private readonly PbtNodeGroupStore _parallelStore = new();
        private readonly EipReferenceTree _oracle = new();
        private ValueHash256 _bucketingRoot;
        private ValueHash256 _sortedRoot;
        private ValueHash256 _parallelRoot;

        public void Apply((byte[] Key, byte[]? Value)[] writes)
        {
            using PbtWriteBatchBuilder<TKey> builder = new(0);
            PbtWriteOperation<TKey>[] sorted = new PbtWriteOperation<TKey>[writes.Length];
            for (int index = 0; index < writes.Length; index++)
            {
                (byte[] key, byte[]? value) = writes[index];
                TKey treeKey = TKey.Create(key);
                ValueHash256 leaf = value is null ? default : new ValueHash256(value);
                builder.Set(treeKey, leaf);
                sorted[index] = new(treeKey, leaf);
                if (value is null) _oracle.Delete(key);
                else _oracle.Insert(key, value);
            }
            Array.Sort(sorted, static (left, right) => left.Key.CompareTo(right.Key));

            _bucketingRoot = TrieUpdater<TKey, TPath>.UpdateRoot(_bucketingStore, _bucketingRoot, builder.Build(), omission);
            _sortedRoot = TrieUpdater<TKey, TPath>.UpdateRootSorted(_sortedStore, _sortedRoot, sorted, omission);
            // A single-operation minimum splits every frame with two touched slots, so even small batches fold in parallel.
            _parallelRoot = TrieUpdater<TKey, TPath>.UpdateRootSorted(_parallelStore, _parallelRoot, sorted.AsMemory(), omission,
                PbtTreeHarness.FoldQuota(), PbtTreeHarness.FanOut(1));

            IReadOnlyList<PbtPhysicalPayload> expected = _bucketingStore.ExportPhysicalPayloads();
            IReadOnlyList<PbtPhysicalPayload> actual = _sortedStore.ExportPhysicalPayloads();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_sortedRoot, Is.EqualTo(_bucketingRoot));
                Assert.That(_parallelRoot, Is.EqualTo(_bucketingRoot));
                Assert.That(_sortedRoot.Bytes.ToArray(), Is.EqualTo(_oracle.Merkelize()));
                Assert.That(actual.Select(Describe), Is.EqualTo(expected.Select(Describe)));
                Assert.That(_parallelStore.ExportPhysicalPayloads().Select(Describe), Is.EqualTo(expected.Select(Describe)));
            }
            PbtStoreTestExtensions.AssertSubtreeBytes(actual);
        }

        public void Dispose()
        {
            _bucketingStore.Dispose();
            _sortedStore.Dispose();
            _parallelStore.Dispose();
        }
    }
}
