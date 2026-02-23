// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class SeqlockCacheTests
{
    /// <summary>
    /// All instances share the same 64-bit hash, forcing every key into the same
    /// cache set with the same header signature. Correctness depends entirely on
    /// the seqlock + Equals protocol — exactly where the ref-readonly bug lives.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SameHashKey(int id) : IHash64bit<SameHashKey>
    {
        public int Id = id;
        // Padding to ~48 bytes so the struct copy is non-trivial.
        private long _pad0, _pad1, _pad2, _pad3, _pad4;

        public readonly long GetHashCode64() => unchecked((long)0xDEAD_BEEF_CAFE_BABE);
        public readonly bool Equals(in SameHashKey other) => Id == other.Id;
    }

    private static StorageCell CreateKey(int seed)
    {
        byte[] addressBytes = new byte[20];
        new Random(seed).NextBytes(addressBytes);
        return new StorageCell(new Address(addressBytes), new UInt256((ulong)seed));
    }

    private static byte[] CreateValue(int seed)
    {
        byte[] value = new byte[32];
        new Random(seed).NextBytes(value);
        return value;
    }

    [Test]
    public void New_cache_returns_miss()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);

        bool found = cache.TryGetValue(in key, out byte[]? value);

        found.Should().BeFalse();
        value.Should().BeNull();
    }

    [Test]
    public void Set_then_get_returns_value()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);
        byte[] expected = CreateValue(1);

        cache.Set(in key, expected);
        bool found = cache.TryGetValue(in key, out byte[]? value);

        found.Should().BeTrue();
        value.Should().BeSameAs(expected);
    }

    [Test]
    public void Set_overwrites_existing_value()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);
        byte[] first = CreateValue(1);
        byte[] second = CreateValue(2);

        cache.Set(in key, first);
        cache.Set(in key, second);
        bool found = cache.TryGetValue(in key, out byte[]? value);

        found.Should().BeTrue();
        value.Should().BeSameAs(second);
    }

    [Test]
    public void Set_with_same_value_is_noop()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);
        byte[] expected = CreateValue(1);

        cache.Set(in key, expected);
        cache.Set(in key, expected); // Same reference - should be fast-path no-op
        bool found = cache.TryGetValue(in key, out byte[]? value);

        found.Should().BeTrue();
        value.Should().BeSameAs(expected);
    }

    [Test]
    public void Null_value_can_be_stored_and_retrieved()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);

        cache.Set(in key, null);
        bool found = cache.TryGetValue(in key, out byte[]? value);

        found.Should().BeTrue();
        value.Should().BeNull();
    }

    [Test]
    public void GetOrAdd_returns_existing_value()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);
        byte[] expected = CreateValue(1);

        cache.Set(in key, expected);
        byte[]? result = cache.GetOrAdd(in key, static (in StorageCell _) => new byte[32]);

        result.Should().BeSameAs(expected);
    }

    [Test]
    public void GetOrAdd_calls_factory_on_miss()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);
        byte[] factoryResult = CreateValue(1);

        byte[]? result = cache.GetOrAdd(in key, (in StorageCell _) => factoryResult);

        result.Should().BeSameAs(factoryResult);

        // Value should now be cached
        bool found = cache.TryGetValue(in key, out byte[]? cached);
        found.Should().BeTrue();
        cached.Should().BeSameAs(factoryResult);
    }

    [Test]
    public void GetOrAdd_with_func_returns_existing_value()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);
        byte[] expected = CreateValue(1);

        cache.Set(in key, expected);
        byte[]? result = cache.GetOrAdd(in key, static (in _) => new byte[32]);

        result.Should().BeSameAs(expected);
    }

    [Test]
    public void GetOrAdd_with_func_calls_factory_on_miss()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);
        byte[] factoryResult = CreateValue(1);

        byte[]? result = cache.GetOrAdd(in key, (in _) => factoryResult);

        result.Should().BeSameAs(factoryResult);
    }

    [Test]
    public void Clear_invalidates_all_entries()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key1 = CreateKey(1);
        StorageCell key2 = CreateKey(2);

        cache.Set(in key1, CreateValue(1));
        cache.Set(in key2, CreateValue(2));

        cache.Clear();

        cache.TryGetValue(in key1, out _).Should().BeFalse();
        cache.TryGetValue(in key2, out _).Should().BeFalse();
    }

    [Test]
    public void Clear_allows_new_entries()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);
        byte[] beforeClear = CreateValue(1);
        byte[] afterClear = CreateValue(2);

        cache.Set(in key, beforeClear);
        cache.Clear();
        cache.Set(in key, afterClear);

        bool found = cache.TryGetValue(in key, out byte[]? value);
        found.Should().BeTrue();
        value.Should().BeSameAs(afterClear);
    }

    [Test]
    public void Multiple_clears_work()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);

        for (int i = 0; i < 100; i++)
        {
            byte[] value = CreateValue(i);
            cache.Set(in key, value);
            cache.TryGetValue(in key, out byte[]? retrieved).Should().BeTrue();
            retrieved.Should().BeSameAs(value);
            cache.Clear();
            cache.TryGetValue(in key, out _).Should().BeFalse();
        }
    }

    [Test]
    public void Different_keys_can_be_stored()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        const int count = 100;

        StorageCell[] keys = new StorageCell[count];
        byte[][] values = new byte[count][];

        for (int i = 0; i < count; i++)
        {
            keys[i] = CreateKey(i);
            values[i] = CreateValue(i);
            cache.Set(in keys[i], values[i]);
        }

        // Note: This is a direct-mapped cache, so some entries may be evicted
        // due to hash collisions. We just verify that at least some survive.
        int hits = 0;
        for (int i = 0; i < count; i++)
        {
            if (cache.TryGetValue(in keys[i], out byte[]? value) && ReferenceEquals(value, values[i]))
            {
                hits++;
            }
        }

        hits.Should().BeGreaterThan(0, "at least some entries should survive");
    }

    [Test]
    public void Concurrent_reads_are_safe()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);
        byte[] expected = CreateValue(1);
        cache.Set(in key, expected);

        const int threadCount = 8;
        const int iterations = 10000;
        int successCount = 0;

        Parallel.For(0, threadCount, _ =>
        {
            for (int i = 0; i < iterations; i++)
            {
                if (cache.TryGetValue(in key, out byte[]? value) && ReferenceEquals(value, expected))
                {
                    Interlocked.Increment(ref successCount);
                }
            }
        });

        successCount.Should().Be(threadCount * iterations);
    }

    [Test]
    public void Concurrent_writes_do_not_corrupt()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);

        const int threadCount = 8;
        const int iterations = 1000;
        byte[][] values = new byte[threadCount][];
        for (int i = 0; i < threadCount; i++)
        {
            values[i] = CreateValue(i);
        }

        Parallel.For(0, threadCount, t =>
        {
            for (int i = 0; i < iterations; i++)
            {
                cache.Set(in key, values[t]);
            }
        });

        // After concurrent writes, the cache should contain one of the values
        bool found = cache.TryGetValue(in key, out byte[]? result);
        if (found)
        {
            // Value should be one of the values we wrote
            bool isValid = false;
            for (int i = 0; i < threadCount; i++)
            {
                if (ReferenceEquals(result, values[i]))
                {
                    isValid = true;
                    break;
                }
            }
            isValid.Should().BeTrue("cached value should be one of the written values");
        }
    }

    [Test]
    public void Concurrent_read_write_is_safe()
    {
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);
        byte[] value1 = CreateValue(1);
        byte[] value2 = CreateValue(2);

        const int iterations = 10000;
        bool stop = false;

        // Writer thread
        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < iterations && !stop; i++)
            {
                cache.Set(in key, i % 2 == 0 ? value1 : value2);
            }
        });

        // Reader thread
        int validReads = 0;
        int misses = 0;
        Task reader = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                if (cache.TryGetValue(in key, out byte[]? value))
                {
                    // Value should be either value1 or value2
                    if (ReferenceEquals(value, value1) || ReferenceEquals(value, value2))
                    {
                        Interlocked.Increment(ref validReads);
                    }
                }
                else
                {
                    Interlocked.Increment(ref misses);
                }
            }
        });

        Task.WaitAll(writer, reader);
        stop = true;

        // All reads should have returned valid values (or miss due to concurrent write)
        (validReads + misses).Should().Be(iterations);
    }

    [Test]
    public void AddressAsKey_works_with_cache()
    {
        SeqlockCache<AddressAsKey, Account> cache = new();
        Address address = new Address("0x1234567890123456789012345678901234567890");
        AddressAsKey key = address;
        Account account = new Account(100, 1);

        cache.Set(in key, account);
        bool found = cache.TryGetValue(in key, out Account? result);

        found.Should().BeTrue();
        result.Should().BeSameAs(account);
    }

    [Test]
    public void Concurrent_set_same_value_fast_path_is_safe()
    {
        // Tests the fast-path optimization where Set skips write if value matches.
        // This exercises the seqlock protocol in the fast-path to avoid torn reads.
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);
        byte[] value = CreateValue(1);

        cache.Set(in key, value);

        const int threadCount = 8;
        const int iterations = 10000;

        // Multiple threads all trying to set the same key to the same value
        // This hammers the fast-path check
        Parallel.For(0, threadCount, _ =>
        {
            for (int i = 0; i < iterations; i++)
            {
                cache.Set(in key, value);
            }
        });

        // Value should still be retrievable and correct
        bool found = cache.TryGetValue(in key, out byte[]? result);
        found.Should().BeTrue();
        result.Should().BeSameAs(value);
    }

    [Test]
    public void Concurrent_read_write_returns_correct_value_for_key()
    {
        // All keys share the same hash → same 2 slots (way 0 + way 1).
        // The hash-signature check always passes, so correctness relies on seqlock + Equals.
        SeqlockCache<SameHashKey, object> cache = new();

        const int keyCount = 16;
        SameHashKey[] keys = new SameHashKey[keyCount];
        object[] values = new object[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            keys[i] = new SameHashKey(i);
            values[i] = new object(); // unique reference identity
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        string? failure = null;
        int failureCount = 0;

        // 4 writers continuously cycling through all key-value pairs
        Task[] writers = new Task[4];
        for (int w = 0; w < writers.Length; w++)
        {
            writers[w] = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested && failure is null)
                {
                    for (int i = 0; i < keyCount; i++)
                    {
                        SameHashKey k = keys[i];
                        cache.Set(in k, values[i]);
                    }
                }
            });
        }

        // 4 readers: if TryGetValue(key_i) hits, the value MUST be values[i]
        Task[] readers = new Task[4];
        for (int r = 0; r < readers.Length; r++)
        {
            int readerIndex = r;
            readers[r] = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested && failure is null)
                {
                    for (int i = 0; i < keyCount; i++)
                    {
                        SameHashKey k = keys[i];
                        if (cache.TryGetValue(in k, out object? val) && val is not null
                            && !ReferenceEquals(val, values[i]))
                        {
                            Interlocked.Increment(ref failureCount);
                            Interlocked.CompareExchange(
                                ref failure,
                                $"Key[{i}] returned wrong value (reader {readerIndex})",
                                null);
                        }
                    }
                }
            });
        }

        Task.WaitAll(writers);
        Task.WaitAll(readers);

        failure.Should().BeNull($"wrong key-value pairing detected {failureCount} time(s)");
    }

    [Test]
    public void Concurrent_set_alternating_values_is_safe()
    {
        // Tests concurrent writes with different values interleaved with same-value writes.
        // Exercises both the fast-path (same value) and slow-path (different value).
        SeqlockCache<StorageCell, byte[]> cache = new();
        StorageCell key = CreateKey(1);
        byte[][] values = new byte[4][];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = CreateValue(i);
        }

        const int threadCount = 8;
        const int iterations = 5000;

        Parallel.For(0, threadCount, t =>
        {
            for (int i = 0; i < iterations; i++)
            {
                // Each thread cycles through values, creating both fast-path and slow-path scenarios
                cache.Set(in key, values[(t + i) % values.Length]);
            }
        });

        // After all writes, cache should contain one of the valid values
        bool found = cache.TryGetValue(in key, out byte[]? result);
        if (found)
        {
            bool isValid = Array.Exists(values, v => ReferenceEquals(v, result));
            isValid.Should().BeTrue("cached value should be one of the written values");
        }
    }
}
