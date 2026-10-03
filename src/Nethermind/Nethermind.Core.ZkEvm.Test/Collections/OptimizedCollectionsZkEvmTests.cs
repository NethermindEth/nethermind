// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Collections;

public class OptimizedCollectionsZkEvmTests
{
    private const int Steps = 4_000;

    [Test]
    public void Dictionary_matches_bcl_contents_and_order([Values(1, 2, 3)] int seed)
    {
        Random random = new(seed);
        OptimizedDictionary<CollidingKey, int> map = [];
        Dictionary<CollidingKey, int> expected = [];

        for (int step = 0; step < Steps; step++)
        {
            CollidingKey key = new(random.Next(256));
            switch (random.Next(10))
            {
                case < 4:
                    map[key] = step;
                    expected[key] = step;
                    break;
                case < 6:
                    Assert.That(map.Remove(key, out int removed), Is.EqualTo(expected.Remove(key, out int expectedRemoved)));
                    Assert.That(removed, Is.EqualTo(expectedRemoved));
                    break;
                case 6:
                    ref int value = ref map.GetValueRefOrAddDefault(key, out bool exists);
                    Assert.That(exists, Is.EqualTo(expected.ContainsKey(key)));
                    value++;
                    expected[key] = expected.GetValueOrDefault(key) + 1;
                    break;
                case 7:
                    Assert.That(map.TryGetValue(key, out int found), Is.EqualTo(expected.TryGetValue(key, out int expectedFound)));
                    Assert.That(found, Is.EqualTo(expectedFound));
                    break;
                default:
                    if (random.Next(150) == 0)
                    {
                        map.ClearAndTrim(trimAboveCapacity: 16, trimToCapacity: 8);
                        expected.Clear();
                    }
                    else
                    {
                        Assert.That(map.ContainsKey(key), Is.EqualTo(expected.ContainsKey(key)));
                    }

                    break;
            }

            Assert.That(map.Count, Is.EqualTo(expected.Count));
            if (step % 64 == 0) Assert.That(map, Is.EqualTo(expected));
        }

        Assert.That(map, Is.EqualTo(expected));
        Assert.That(map.Keys, Is.EqualTo(expected.Keys));
        Assert.That(map.Values, Is.EqualTo(expected.Values));
    }

    [Test]
    public void Hash_set_matches_bcl_contents_and_order([Values(1, 2, 3)] int seed)
    {
        Random random = new(seed);
        OptimizedHashSet<CollidingKey> set = [];
        HashSet<CollidingKey> expected = [];

        for (int step = 0; step < Steps; step++)
        {
            CollidingKey item = new(random.Next(256));
            switch (random.Next(10))
            {
                case < 5:
                    Assert.That(set.Add(item), Is.EqualTo(expected.Add(item)));
                    break;
                case < 8:
                    Assert.That(set.Remove(item), Is.EqualTo(expected.Remove(item)));
                    break;
                default:
                    if (random.Next(100) == 0)
                    {
                        set.Clear();
                        expected.Clear();
                    }
                    else
                    {
                        Assert.That(set.Contains(item), Is.EqualTo(expected.Contains(item)));
                    }

                    break;
            }

            Assert.That(set.Count, Is.EqualTo(expected.Count));
            if (step % 64 == 0) Assert.That(set, Is.EqualTo(expected));
        }

        Assert.That(set, Is.EqualTo(expected));
    }

    [Test]
    public void Clear_of_a_sparse_table_forgets_every_item()
    {
        OptimizedHashSet<CollidingKey> set = new(1_024);
        Assert.That(set.Add(new CollidingKey(1)) && set.Add(new CollidingKey(9)) && set.Remove(new CollidingKey(1)), Is.True);

        set.Clear();
        set.Add(new CollidingKey(17));

        Assert.That(set.Contains(new CollidingKey(9)), Is.False);
        Assert.That(set, Is.EqualTo(new[] { new CollidingKey(17) }));
    }

    [Test]
    public void Collections_use_the_given_comparer_and_accept_null_items()
    {
        OptimizedDictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        OptimizedHashSet<string?> set = new(StringComparer.OrdinalIgnoreCase);
        map["Key"] = 1;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(set.Add("Item") && set.Add(null), Is.True);
            Assert.That(map["KEY"], Is.EqualTo(1));
            Assert.That(set.Contains("ITEM"), Is.True);
            Assert.That(set.Contains(null), Is.True);
            Assert.That(set.Add("item"), Is.False);
        }
    }

    [Test]
    public void Dictionary_allows_removal_while_enumerating()
    {
        OptimizedDictionary<CollidingKey, int> map = [];
        for (int i = 0; i < 20; i++) map[new CollidingKey(i)] = i;

        List<int> seen = [];
        foreach (CollidingKey key in map.Keys)
        {
            seen.Add(key.Value);
            map.Remove(key);
        }

        Assert.That(seen, Is.EqualTo(Enumerable.Range(0, 20)));
        Assert.That(map.Count, Is.Zero);
    }

    [Test]
    public void Journal_set_restore_and_clear_forget_the_dropped_items()
    {
        JournalSet<CollidingKey> journal = new(EqualityComparer<CollidingKey>.Default);
        for (int i = 0; i < 10; i++) journal.Add(new CollidingKey(i));
        int snapshot = journal.TakeSnapshot();
        for (int i = 10; i < 20; i++) journal.Add(new CollidingKey(i));

        journal.Restore(snapshot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(journal.Count, Is.EqualTo(10));
            Assert.That(journal.Contains(new CollidingKey(5)), Is.True);
            Assert.That(journal.Contains(new CollidingKey(15)), Is.False);
        }

        journal.Clear();

        Assert.That(journal.Contains(new CollidingKey(5)), Is.False);
        Assert.That(journal.Add(new CollidingKey(15)), Is.True);
    }

    /// <summary>A key whose hash codes collide in groups of 32, so lookups walk bucket chains.</summary>
    private readonly record struct CollidingKey(int Value)
    {
        public override int GetHashCode() => Value & 7;
    }
}
