// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Collections;

/// <summary>
/// Covers the guest <see cref="OptimizedDictionary{TKey,TValue}"/> and <see cref="OptimizedHashSet{T}"/>, which the host
/// suite cannot reach because the host build is the BCL collection itself.
/// </summary>
/// <remarks>
/// Each case replays one random operation sequence against the guest collection and the BCL one and requires the same
/// contents in the same enumeration order after every step: host and guest must observe identical iteration, including
/// how removed slots are reused, chains collide and storage is trimmed.
/// </remarks>
public class OptimizedDictionaryZkEvmTests
{
    private const int Operations = 4_000;

    [Test]
    public void Dictionary_matches_the_bcl_dictionary([Values(1, 2, 3)] int seed, [Values(16, 600)] int keyRange)
    {
        Random random = new(seed);
        OptimizedDictionary<CollidingKey, int> map = [];
        Dictionary<CollidingKey, int> expected = [];

        for (int step = 0; step < Operations; step++)
        {
            CollidingKey key = new(random.Next(keyRange));
            int value = random.Next();
            switch (random.Next(100))
            {
                case < 30:
                    ref int slot = ref map.GetValueRefOrAddDefault(key, out bool exists);
                    Assert.That(exists, Is.EqualTo(expected.ContainsKey(key)));
                    slot = value;
                    expected[key] = value;
                    break;
                case < 45:
                    Assert.That(map.TryAdd(key, value), Is.EqualTo(expected.TryAdd(key, value)));
                    break;
                case < 55:
                    map[key] = value;
                    expected[key] = value;
                    break;
                case < 80:
                    Assert.That(map.Remove(key, out int removed), Is.EqualTo(expected.Remove(key, out int expectedRemoved)));
                    Assert.That(removed, Is.EqualTo(expectedRemoved));
                    break;
                case < 82:
                    map.Clear();
                    expected.Clear();
                    break;
                case < 84:
                    map.ClearAndTrim(trimAboveCapacity: 8, trimToCapacity: 4);
                    expected.ClearAndTrim(trimAboveCapacity: 8, trimToCapacity: 4);
                    break;
                default:
                    Assert.That(map.TryGetValue(key, out int found), Is.EqualTo(expected.TryGetValue(key, out int expectedFound)));
                    Assert.That(found, Is.EqualTo(expectedFound));
                    break;
            }

            AssertSameContents(map, expected);
        }
    }

    [Test]
    public void Set_matches_the_bcl_set([Values(1, 2, 3)] int seed)
    {
        Random random = new(seed);
        OptimizedHashSet<CollidingKey> set = [];
        HashSet<CollidingKey> expected = [];

        for (int step = 0; step < Operations; step++)
        {
            CollidingKey item = new(random.Next(64));
            switch (random.Next(10))
            {
                case < 5:
                    Assert.That(set.Add(item), Is.EqualTo(expected.Add(item)));
                    break;
                case < 8:
                    Assert.That(set.Remove(item), Is.EqualTo(expected.Remove(item)));
                    break;
                case 8:
                    Assert.That(set.Contains(item), Is.EqualTo(expected.Contains(item)));
                    break;
                default:
                    set.ClearAndTrim(trimAboveCapacity: 8, trimToCapacity: 4);
                    expected.ClearAndTrim(trimAboveCapacity: 8, trimToCapacity: 4);
                    break;
            }

            Assert.That(set.Count, Is.EqualTo(expected.Count));
            Assert.That(new List<CollidingKey>(set), Is.EqualTo(new List<CollidingKey>(expected)));
        }
    }

    [Test]
    public void Enumerator_reaches_each_entry_in_place()
    {
        OptimizedDictionary<CollidingKey, int> map = [];
        for (int i = 0; i < 40; i++) map[new CollidingKey(i)] = i;
        for (int i = 0; i < 40; i += 3) map.Remove(new CollidingKey(i));
        List<KeyValuePair<CollidingKey, int>> pairs = [.. map];

        List<KeyValuePair<CollidingKey, int>> visited = [];
        OptimizedDictionary<CollidingKey, int>.Enumerator entries = map.GetEnumerator();
        while (entries.MoveNext())
        {
            visited.Add(new(entries.CurrentKey, entries.CurrentValue));
            entries.CurrentValue = -entries.CurrentKey.Value;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(visited, Is.EqualTo(pairs));
            foreach (KeyValuePair<CollidingKey, int> pair in pairs) Assert.That(map[pair.Key], Is.EqualTo(-pair.Key.Value));
        }
    }

    [Test]
    public void Reference_keys_hash_and_compare_by_value()
    {
        OptimizedDictionary<string, int> map = new(EqualityComparer<string>.Default) { ["a"] = 1 };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(map[new string('a', 1)], Is.EqualTo(1));
            Assert.That(map.ContainsKey("b"), Is.False);
            Assert.Throws<KeyNotFoundException>(() => _ = map["b"]);
            Assert.Throws<ArgumentException>(() => map.Add("a", 2));
        }
    }

    [Test]
    public void Rejects_a_comparer_that_overrides_the_key_equality() =>
        Assert.Throws<NotSupportedException>(() => _ = new OptimizedDictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    private static void AssertSameContents(OptimizedDictionary<CollidingKey, int> map, Dictionary<CollidingKey, int> expected)
    {
        Assert.That(map.Count, Is.EqualTo(expected.Count));
        Assert.That(new List<KeyValuePair<CollidingKey, int>>(map), Is.EqualTo(new List<KeyValuePair<CollidingKey, int>>(expected)));
    }

    /// <summary>A key whose hash keeps only a few bits, so chains of several entries share each bucket.</summary>
    private readonly record struct CollidingKey(int Value)
    {
        public override int GetHashCode() => Value & 7;
    }
}
