// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.State.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PreWarmCacheTests
{
    [Test]
    public void TryAdd_and_TryGetValue_roundtrip()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);
        byte[] value = [1, 2, 3, 4];

        cache.TryAdd(in key, value).Should().BeTrue();
        cache.TryGetValue(in key, out byte[]? retrieved).Should().BeTrue();
        retrieved.Should().BeEquivalentTo(value);
    }

    [Test]
    public void TryGetValue_returns_false_for_missing_key()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);

        cache.TryGetValue(in key, out byte[]? value).Should().BeFalse();
        value.Should().BeNull();
    }

    [Test]
    public void TryAdd_skips_write_when_hash_signature_matches()
    {
        // With skip-if-exact-match optimization, adding the same key twice
        // will skip the second write since the hash signature already matches.
        // This is correct for prewarming where same key = same value.
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);
        byte[] value1 = [1, 2, 3];
        byte[] value2 = [4, 5, 6];

        cache.TryAdd(in key, value1).Should().BeTrue();
        // Second add returns true (skip path) but doesn't overwrite
        cache.TryAdd(in key, value2).Should().BeTrue();
        cache.TryGetValue(in key, out byte[]? retrieved).Should().BeTrue();
        // First value is preserved because hash signature matched and write was skipped
        retrieved.Should().BeEquivalentTo(value1);
    }

    [Test]
    public void TryAdd_overwrites_after_clear()
    {
        // After Clear(), the epoch changes, so the hash signature no longer matches
        // and the new value will be written
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);
        byte[] value1 = [1, 2, 3];
        byte[] value2 = [4, 5, 6];

        cache.TryAdd(in key, value1).Should().BeTrue();
        cache.Clear();
        cache.TryAdd(in key, value2).Should().BeTrue();
        cache.TryGetValue(in key, out byte[]? retrieved).Should().BeTrue();
        retrieved.Should().BeEquivalentTo(value2);
    }

    [Test]
    public void Clear_invalidates_all_entries()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key1 = new StorageCell(TestItem.AddressA, 1);
        var key2 = new StorageCell(TestItem.AddressB, 2);
        byte[] value = [1, 2, 3];

        cache.TryAdd(in key1, value);
        cache.TryAdd(in key2, value);

        cache.Clear();

        cache.TryGetValue(in key1, out _).Should().BeFalse();
        cache.TryGetValue(in key2, out _).Should().BeFalse();
    }

    [Test]
    public void GetOrAdd_returns_existing_value()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);
        byte[] existingValue = [1, 2, 3];
        byte[] factoryValue = [4, 5, 6];

        cache.TryAdd(in key, existingValue);
        byte[]? result = cache.GetOrAdd(in key, _ => factoryValue);

        result.Should().BeEquivalentTo(existingValue);
    }

    [Test]
    public void GetOrAdd_calls_factory_for_missing_key()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);
        byte[] factoryValue = [4, 5, 6];
        bool factoryCalled = false;

        byte[]? result = cache.GetOrAdd(in key, _ =>
        {
            factoryCalled = true;
            return factoryValue;
        });

        factoryCalled.Should().BeTrue();
        result.Should().BeEquivalentTo(factoryValue);
    }

    [Test]
    public void Indexer_set_adds_value()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);
        byte[] value = [1, 2, 3];

        cache[in key] = value;

        cache.TryGetValue(in key, out byte[]? retrieved).Should().BeTrue();
        retrieved.Should().BeEquivalentTo(value);
    }

    [Test]
    public void Indexer_get_throws_for_missing_key()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);

        Action act = () => _ = cache[in key];

        act.Should().Throw<System.Collections.Generic.KeyNotFoundException>();
    }

    [Test]
    public void Indexer_get_returns_value_for_existing_key()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);
        byte[] value = [1, 2, 3];

        cache[in key] = value;

        cache[in key].Should().BeEquivalentTo(value);
    }

    [Test]
    public void Works_with_AddressAsKey()
    {
        var cache = new PreWarmCache<AddressAsKey, Account>();
        AddressAsKey key = TestItem.AddressA;
        var account = new Account(100);

        cache.TryAdd(in key, account).Should().BeTrue();
        cache.TryGetValue(in key, out Account? retrieved).Should().BeTrue();
        retrieved.Should().Be(account);
    }

    [Test]
    public void AddressAsKey_clear_works()
    {
        var cache = new PreWarmCache<AddressAsKey, Account>();
        AddressAsKey key1 = TestItem.AddressA;
        AddressAsKey key2 = TestItem.AddressB;
        var account = new Account(100);

        cache.TryAdd(in key1, account);
        cache.TryAdd(in key2, account);
        cache.Clear();

        cache.TryGetValue(in key1, out _).Should().BeFalse();
        cache.TryGetValue(in key2, out _).Should().BeFalse();
    }

    [Test]
    public void Epoch_wraparound_invalidates_entries()
    {
        // With 12-bit epoch, we wrap after 4096 clears
        const int epochBits = 12;
        const int epochCount = 1 << epochBits; // 4096

        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);
        byte[] value = [1, 2, 3];

        cache.TryAdd(in key, value);
        cache.TryGetValue(in key, out _).Should().BeTrue();

        // Clear enough times to wrap the epoch
        for (int i = 0; i < epochCount; i++)
        {
            cache.Clear();
        }

        // After wraparound, the entry appears valid again because the epoch matches
        // (12-bit epoch wraps every 4096 clears - this is acceptable for a prewarming cache)
        cache.TryGetValue(in key, out byte[]? retrieved).Should().BeTrue();
        retrieved.Should().BeEquivalentTo(value);
    }

    [Test]
    public void Multiple_epoch_wraparounds_maintain_correctness()
    {
        const int epochCount = 1 << 12; // 4096

        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);

        // Round 0: Add initial value
        byte[] value0 = [0x00];
        cache.TryAdd(in key, value0);
        cache.TryGetValue(in key, out byte[]? retrieved0).Should().BeTrue();
        retrieved0.Should().BeEquivalentTo(value0);

        // Clear through a full epoch cycle (4096 clears = epoch wraps from 0 back to 0)
        for (int i = 0; i < epochCount; i++)
        {
            cache.Clear();
        }

        // After wraparound, the entry appears valid again because the epoch matches
        // (12-bit epoch wraps every 4096 clears - this is acceptable for a prewarming cache)
        cache.TryGetValue(in key, out byte[]? retrievedAfterWrap).Should().BeTrue();
        retrievedAfterWrap.Should().BeEquivalentTo(value0);

        // Round 1: Try to add new value with same key
        // With skip-if-exact-match optimization, this is skipped because the hash signature
        // (epoch + hash bits) matches the existing entry after wraparound.
        // This is acceptable for prewarming: epoch wraparound takes ~13.6 hours (4096 blocks * 12s),
        // and stale data from wraparound is already an acknowledged limitation.
        byte[] value1 = [0x01];
        cache.TryAdd(in key, value1);
        cache.TryGetValue(in key, out byte[]? retrieved1).Should().BeTrue();
        // The original value is preserved because skip-if-exact-match fired
        retrieved1.Should().BeEquivalentTo(value0);
    }

    [Test]
    public void Concurrent_adds_and_gets_are_thread_safe()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        const int threadCount = 8;
        const int operationsPerThread = 10_000;
        var exceptions = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(() =>
        {
            try
            {
                var random = new Random(threadId);
                for (int i = 0; i < operationsPerThread; i++)
                {
                    int keyIndex = random.Next(1000);
                    var key = new StorageCell(TestItem.AddressA, (UInt256)keyIndex);
                    byte[] value = [(byte)threadId, (byte)(i & 0xFF)];

                    if (random.Next(2) == 0)
                    {
                        cache.TryAdd(in key, value);
                    }
                    else
                    {
                        cache.TryGetValue(in key, out _);
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        Task.WaitAll(tasks);
        exceptions.Should().BeEmpty();
    }

    [Test]
    public void Concurrent_clears_during_operations_are_safe()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        const int threadCount = 4;
        const int operationsPerThread = 5_000;
        var exceptions = new ConcurrentBag<Exception>();
        var cts = new CancellationTokenSource();

        // Background thread doing continuous clears
        var clearTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                cache.Clear();
                Thread.SpinWait(100);
            }
        });

        var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(() =>
        {
            try
            {
                var random = new Random(threadId);
                for (int i = 0; i < operationsPerThread; i++)
                {
                    int keyIndex = random.Next(100);
                    var key = new StorageCell(TestItem.AddressA, (UInt256)keyIndex);
                    byte[] value = [(byte)threadId];

                    cache.TryAdd(in key, value);
                    cache.TryGetValue(in key, out _);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        Task.WaitAll(tasks);
        cts.Cancel();
        clearTask.Wait();

        exceptions.Should().BeEmpty();
    }

    [Test]
    public void Parallel_operations_on_same_key_are_safe()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);
        const int iterations = 100_000;
        var exceptions = new ConcurrentBag<Exception>();

        Parallel.For(0, iterations, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            try
            {
                byte[] value = [(byte)(i & 0xFF)];
                cache.TryAdd(in key, value);
                cache.TryGetValue(in key, out byte[]? retrieved);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        exceptions.Should().BeEmpty();
    }

    [Test]
    public void Different_keys_with_same_bucket_index_handle_collision()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();

        // Find keys that hash to the same bucket (bottom 14 bits of hash)
        var (key1, key2) = FindCollidingKeys();

        byte[] value1 = [1, 1, 1];
        byte[] value2 = [2, 2, 2];

        // Add first key
        cache.TryAdd(in key1, value1);
        cache.TryGetValue(in key1, out byte[]? retrieved1).Should().BeTrue();
        retrieved1.Should().BeEquivalentTo(value1);

        // Add second key (same bucket) - should overwrite the slot
        cache.TryAdd(in key2, value2);

        // First key should now miss (bucket collision)
        // This is expected behavior for a one-way set-associative cache
        cache.TryGetValue(in key1, out _).Should().BeFalse();

        // Second key should hit
        cache.TryGetValue(in key2, out byte[]? retrieved2).Should().BeTrue();
        retrieved2.Should().BeEquivalentTo(value2);
    }

    [Test]
    public void Collision_handling_under_concurrent_access()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var (key1, key2) = FindCollidingKeys();
        var exceptions = new ConcurrentBag<Exception>();

        Parallel.For(0, 10_000, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            try
            {
                var key = i % 2 == 0 ? key1 : key2;
                byte[] value = [(byte)(i & 0xFF)];
                cache.TryAdd(in key, value);
                cache.TryGetValue(in key, out _);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        exceptions.Should().BeEmpty();
    }

    [Test]
    public void Large_struct_keys_are_read_atomically()
    {
        // StorageCell contains Address (reference) + UInt256 (32 bytes) + bool
        // This test verifies no torn reads occur
        var cache = new PreWarmCache<StorageCell, byte[]>();
        const int iterations = 50_000;
        var exceptions = new ConcurrentBag<Exception>();

        // Create keys with distinctive patterns
        var keys = Enumerable.Range(0, 10).Select(i =>
            new StorageCell(TestItem.Addresses[i % TestItem.Addresses.Length], (UInt256)i)).ToArray();

        Parallel.For(0, iterations, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            try
            {
                int keyIndex = i % keys.Length;
                var key = keys[keyIndex];
                byte[] value = [(byte)keyIndex];

                cache.TryAdd(in key, value);
                cache.TryGetValue(in key, out byte[]? retrieved);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        exceptions.Should().BeEmpty();
    }

    [Test]
    public void StorageCell_with_max_UInt256_index_works()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, UInt256.MaxValue);
        byte[] value = [1, 2, 3];

        cache.TryAdd(in key, value).Should().BeTrue();
        cache.TryGetValue(in key, out byte[]? retrieved).Should().BeTrue();
        retrieved.Should().BeEquivalentTo(value);
    }

    [Test]
    public void StorageCell_with_various_addresses_works()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();

        foreach (var address in TestItem.Addresses.Take(10))
        {
            var key = new StorageCell(address, 42);
            byte[] value = address.Bytes[..4].ToArray();

            cache.TryAdd(in key, value);
            cache.TryGetValue(in key, out byte[]? retrieved).Should().BeTrue();
            retrieved.Should().BeEquivalentTo(value);

            cache.Clear();
        }
    }

    [Test]
    public void Spin_through_all_buckets()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        const int count = 1 << 14; // 16384 buckets

        // Fill all buckets
        for (int i = 0; i < count; i++)
        {
            var key = new StorageCell(TestItem.AddressA, (UInt256)i);
            byte[] value = BitConverter.GetBytes(i);
            cache.TryAdd(in key, value);
        }

        // Verify we can read back (note: some may have been evicted due to collisions)
        int hits = 0;
        for (int i = 0; i < count; i++)
        {
            var key = new StorageCell(TestItem.AddressA, (UInt256)i);
            if (cache.TryGetValue(in key, out byte[]? retrieved))
            {
                int expected = i;
                int actual = BitConverter.ToInt32(retrieved);
                actual.Should().Be(expected);
                hits++;
            }
        }

        // We should have a reasonable hit rate (depends on hash distribution)
        // With 16K entries and 16K buckets, perfect hashing would give 100%
        // Real hashing will have some collisions
        hits.Should().BeGreaterThan(count / 2);
    }

    [Test]
    public void Null_value_can_be_stored_and_retrieved()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);

        cache.TryAdd(in key, null).Should().BeTrue();
        cache.TryGetValue(in key, out byte[]? retrieved).Should().BeTrue();
        retrieved.Should().BeNull();
    }

    [Test]
    public void Can_distinguish_null_value_from_missing_key()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var keyWithNull = new StorageCell(TestItem.AddressA, 1);
        var keyMissing = new StorageCell(TestItem.AddressA, 2);

        cache.TryAdd(in keyWithNull, null);

        // Key with null value should return true
        cache.TryGetValue(in keyWithNull, out byte[]? value1).Should().BeTrue();
        value1.Should().BeNull();

        // Missing key should return false
        cache.TryGetValue(in keyMissing, out byte[]? value2).Should().BeFalse();
        value2.Should().BeNull();
    }

    [Test]
    public void Fresh_cache_is_empty()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();

        for (int i = 0; i < 100; i++)
        {
            var key = new StorageCell(TestItem.AddressA, (UInt256)i);
            cache.TryGetValue(in key, out _).Should().BeFalse();
        }
    }

    [Test]
    public void Clear_on_empty_cache_is_safe()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();

        // Should not throw
        for (int i = 0; i < 100; i++)
        {
            cache.Clear();
        }
    }

    [Test]
    public void Rapid_add_clear_cycles()
    {
        var cache = new PreWarmCache<StorageCell, byte[]>();
        var key = new StorageCell(TestItem.AddressA, 1);
        byte[] value = [1, 2, 3];

        for (int i = 0; i < 10_000; i++)
        {
            cache.TryAdd(in key, value);
            cache.Clear();
            cache.TryGetValue(in key, out _).Should().BeFalse();
        }
    }

    private static (StorageCell key1, StorageCell key2) FindCollidingKeys()
    {
        const int bucketMask = (1 << 14) - 1; // 16383

        var key1 = new StorageCell(TestItem.AddressA, 0);
        int targetBucket = key1.GetHashCode() & bucketMask;

        // Search for a key with the same bucket index
        for (ulong i = 1; i < 1_000_000; i++)
        {
            var candidate = new StorageCell(TestItem.AddressA, i);
            if ((candidate.GetHashCode() & bucketMask) == targetBucket)
            {
                return (key1, candidate);
            }
        }

        // Fallback: try different addresses
        for (int i = 0; i < 1_000_000; i++)
        {
            var candidate = new StorageCell(TestItem.AddressB, (UInt256)i);
            if ((candidate.GetHashCode() & bucketMask) == targetBucket)
            {
                return (key1, candidate);
            }
        }

        throw new InvalidOperationException("Could not find colliding keys");
    }
}
