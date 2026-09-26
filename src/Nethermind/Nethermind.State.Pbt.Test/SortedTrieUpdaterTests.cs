// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>Checks the sorted zone fold of the partitioned <see cref="TrieUpdater"/> serially and split across threads, group for group, against the reference tree.</summary>
public class SortedTrieUpdaterTests
{
    [Test]
    public void Random_batches_match_reference(
        [Values] PbtPrefixlessBranchOmission omission,
        [Values(1, 2, 3)] int seed)
    {
        using DifferentialTree tree = new(omission);
        foreach ((byte[] Key, byte[]? Value)[] writes in RandomRounds(new Random(seed), ZoneKey)) tree.Apply(writes);
    }

    /// <summary>Batches of writes, each <c>key=value</c> or a bare key to delete, keyed below a zone byte.</summary>
    private static IEnumerable<TestCaseData> Shapes()
    {
        yield return Shape("Empty_to_one_to_two_leaves_and_back",
            ["0102=11"],
            ["0103=22"],
            ["0102", "0103"]);
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
    public void Targeted_shapes_match_reference(string[][] batches)
    {
        foreach (string zone in new[] { "00", "01", "FF" })
            foreach (PbtPrefixlessBranchOmission omission in Enum.GetValues<PbtPrefixlessBranchOmission>())
            {
                using DifferentialTree tree = new(omission);
                foreach (string[] batch in batches)
                    tree.Apply([.. batch.Select(write => write.Split('=') is [string key, string value]
                        ? (PbtStoreTestExtensions.ZoneKey(zone + key), Value(Bytes.FromHexString(value)[0]))
                        : (PbtStoreTestExtensions.ZoneKey(zone + write), (byte[]?)null))]);
            }
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

    /// <summary>Folds every batch through the partitioned driver serially and with every frame split across threads, each on its own store, and asserts identical roots and groups matching the reference tree.</summary>
    private sealed class DifferentialTree(PbtPrefixlessBranchOmission omission) : IDisposable
    {
        private readonly PbtNodeGroupStore _serialStore = new();
        private readonly PbtNodeGroupStore _parallelStore = new();
        private readonly EipReferenceTree _oracle = new();
        private ValueHash256 _serialRoot;
        private ValueHash256 _parallelRoot;

        public void Apply((byte[] Key, byte[]? Value)[] writes)
        {
            foreach ((byte[] key, byte[]? value) in writes)
            {
                if (value is null) _oracle.Delete(key);
                else _oracle.Insert(key, value);
            }

            // A quota of one folds every zone and frame on the calling thread.
            using (PbtPartitionBatches changes = PbtStoreTestExtensions.PreparePartitions(writes))
                _serialRoot = TrieUpdater.UpdateRoot(_serialStore, _serialRoot, changes, new ConcurrencyController(1), PbtTreeHarness.DefaultFanOut, omission, null);
            // A single-operation minimum splits every frame with two touched slots, so even small batches fold in parallel.
            _parallelRoot = _parallelStore.Fold(_parallelRoot, writes, omission, PbtTreeHarness.FanOut(1), null);

            IReadOnlyList<PbtPhysicalPayload> expected = _serialStore.ExportPhysicalPayloads();
            IReadOnlyList<PbtPhysicalPayload> actual = _parallelStore.ExportPhysicalPayloads();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_parallelRoot, Is.EqualTo(_serialRoot));
                Assert.That(_serialRoot.Bytes.ToArray(), Is.EqualTo(_oracle.Merkelize()));
                Assert.That(actual.Select(Describe), Is.EqualTo(expected.Select(Describe)));
            }
            PbtStoreTestExtensions.AssertSubtreeBytes(actual);
        }

        public void Dispose()
        {
            _serialStore.Dispose();
            _parallelStore.Dispose();
        }
    }
}
