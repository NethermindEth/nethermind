// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

/// <remarks>
/// Replays random operations against the BCL collection and compares every answer and the enumeration order.
/// Also compiled into the zkEVM test project, where the optimized collections are the guest's own tables.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class OptimizedCollectionsTests
{
    private const int Steps = 5_000;
    private const int KeyRange = 300;

    public enum KeyKind
    {
        ValueType,
        CollidingValueType,
        ReferenceType,
    }

    [Test]
    public void Dictionary_matches_bcl([Values] KeyKind kind, [Values(1, 2, 3)] int seed)
    {
        switch (kind)
        {
            case KeyKind.ValueType:
                RunDictionary(static i => new AddressAsKey(NumberedAddress(i)), AddressAsKey.EqualityComparer, seed);
                break;
            case KeyKind.CollidingValueType:
                RunDictionary(static i => new CollidingKey(i), null, seed);
                break;
            default:
                RunDictionary(static i => i.ToString(), null, seed);
                break;
        }
    }

    [Test]
    public void Hash_set_matches_bcl([Values] KeyKind kind, [Values(1, 2, 3)] int seed)
    {
        switch (kind)
        {
            case KeyKind.ValueType:
                RunHashSet(static i => new AddressAsKey(NumberedAddress(i)), AddressAsKey.EqualityComparer, seed);
                break;
            case KeyKind.CollidingValueType:
                RunHashSet(static i => new CollidingKey(i), null, seed);
                break;
            default:
                RunHashSet(static i => i.ToString(), null, seed);
                break;
        }
    }

    [Test]
    public void Dictionary_allows_removing_while_enumerating()
    {
        Dictionary<int, int> expected = [];
        OptimizedDictionary<int, int> actual = [];
        for (int i = 0; i < 100; i++)
        {
            expected.Add(i, i);
            actual.Add(i, i);
        }

        foreach (int key in expected.Keys) if (key % 3 == 0) expected.Remove(key);
        foreach (int key in actual.Keys) if (key % 3 == 0) actual.Remove(key);

        Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()));
    }

    [Test]
    public void Dictionary_reports_missing_null_and_duplicate_keys()
    {
        OptimizedDictionary<int, int> dictionary = new() { { 1, 1 } };
        OptimizedDictionary<string, int> byName = [];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => dictionary[2], Throws.InstanceOf<KeyNotFoundException>());
            Assert.That(() => dictionary.Add(1, 2), Throws.ArgumentException);
            Assert.That(Unsafe.IsNullRef(ref dictionary.GetValueRefOrNullRef(2)), Is.True);
            Assert.That(() => byName[null!] = 1, Throws.ArgumentNullException);
            Assert.That(() => byName.ContainsKey(null!), Throws.ArgumentNullException);
            Assert.That(() => byName.Remove(null!), Throws.ArgumentNullException);
        }
    }

    private static void RunDictionary<TKey>(Func<int, TKey> key, IEqualityComparer<TKey>? comparer, int seed) where TKey : notnull, IEquatable<TKey>
    {
        Random random = new(seed);
        Dictionary<TKey, int> expected = new(comparer);
        OptimizedDictionary<TKey, int> actual = new(comparer);
        for (int step = 0; step < Steps; step++)
        {
            TKey k = key(random.Next(KeyRange));
            int value = random.Next();
            switch (random.Next(10))
            {
                case 0:
                    Assert.That(actual.TryAdd(k, value), Is.EqualTo(expected.TryAdd(k, value)));
                    break;
                case 1:
                    {
                        ref int actualSlot = ref actual.GetValueRefOrAddDefault(k, out bool actualExists);
                        ref int expectedSlot = ref CollectionsMarshal.GetValueRefOrAddDefault(expected, k, out bool expectedExists);
                        Assert.That((actualExists, actualSlot), Is.EqualTo((expectedExists, expectedSlot)));
                        actualSlot = expectedSlot = value;
                        break;
                    }
                case 2:
                    {
                        bool removed = actual.Remove(k, out int actualValue);
                        Assert.That((removed, actualValue), Is.EqualTo((expected.Remove(k, out int expectedValue), expectedValue)));
                        break;
                    }
                case 3:
                    Assert.That(actual.Remove(k), Is.EqualTo(expected.Remove(k)));
                    break;
                case 4:
                    {
                        ref int actualSlot = ref actual.GetValueRefOrNullRef(k);
                        ref int expectedSlot = ref CollectionsMarshal.GetValueRefOrNullRef(expected, k);
                        Assert.That(Unsafe.IsNullRef(ref actualSlot), Is.EqualTo(Unsafe.IsNullRef(ref expectedSlot)));
                        if (!Unsafe.IsNullRef(ref actualSlot)) Assert.That(actualSlot, Is.EqualTo(expectedSlot));
                        break;
                    }
                case 5:
                    {
                        bool found = actual.TryGetValue(k, out int actualValue);
                        Assert.That((found, actualValue, actual.ContainsKey(k)), Is.EqualTo((expected.TryGetValue(k, out int expectedValue), expectedValue, expected.ContainsKey(k))));
                        break;
                    }
                case 6:
                    actual[k] = expected[k] = value;
                    break;
                case 7:
                    if (random.Next(40) == 0)
                    {
                        actual.ClearAndTrim(64, 16);
                        expected.ClearAndTrim(64, 16);
                    }
                    else if (random.Next(40) == 0)
                    {
                        int capacity = expected.Count + random.Next(KeyRange);
                        actual.EnsureCapacity(capacity);
                        expected.EnsureCapacity(capacity);
                    }

                    break;
                default:
                    if (expected.ContainsKey(k)) Assert.That(actual[k], Is.EqualTo(expected[k]));
                    break;
            }

            Assert.That(actual.Count, Is.EqualTo(expected.Count));
            if (step % 50 == 0)
            {
                Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()), "enumeration order");
                Assert.That(actual.Keys.ToArray(), Is.EqualTo(expected.Keys.ToArray()));
                Assert.That(actual.Values.ToArray(), Is.EqualTo(expected.Values.ToArray()));
            }
        }
    }

    private static void RunHashSet<T>(Func<int, T> item, IEqualityComparer<T>? comparer, int seed) where T : IEquatable<T>
    {
        Random random = new(seed);
        HashSet<T> expected = new(comparer);
        OptimizedHashSet<T> actual = new(comparer);
        for (int step = 0; step < Steps; step++)
        {
            T value = item(random.Next(KeyRange));
            switch (random.Next(5))
            {
                case 0:
                case 1:
                    Assert.That(actual.Add(value), Is.EqualTo(expected.Add(value)));
                    break;
                case 2:
                    Assert.That(actual.Remove(value), Is.EqualTo(expected.Remove(value)));
                    break;
                case 3:
                    if (random.Next(40) == 0)
                    {
                        actual.ClearAndTrim(64, 16);
                        expected.ClearAndTrim(64, 16);
                    }

                    break;
                default:
                    Assert.That(actual.Contains(value), Is.EqualTo(expected.Contains(value)));
                    break;
            }

            Assert.That(actual.Count, Is.EqualTo(expected.Count));
            if (step % 50 == 0) Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()), "enumeration order");
        }
    }

    /// <summary>A fresh instance per call, so equal keys are compared by their bytes.</summary>
    private static Address NumberedAddress(int i)
    {
        byte[] bytes = new byte[Address.Size];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(Address.Size - sizeof(int)), i);
        return new Address(bytes);
    }

    /// <summary>Puts every key in one of four chains.</summary>
    private readonly record struct CollidingKey(int Value)
    {
        public override int GetHashCode() => Value & 3;
    }
}
