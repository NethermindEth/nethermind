// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Single-operation latency comparison: LruCache vs ClockCache vs AssociativeCache.
/// </summary>
[MemoryDiagnoser]
public class AssociativeCacheSingleOpBenchmarks
{
    private LruCache<AddressAsKey, Account> _lru = null!;
    private ClockCache<AddressAsKey, Account> _clock = null!;
    private AssociativeCache<AddressAsKey, Account> _assoc = null!;

    private AddressAsKey[] _keys = null!;
    private Account[] _accounts = null!;
    private AddressAsKey _missKey;

    [Params(1000)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _lru = new LruCache<AddressAsKey, Account>(KeyCount, "benchmark");
        _clock = new ClockCache<AddressAsKey, Account>(KeyCount);
        _assoc = new AssociativeCache<AddressAsKey, Account>(KeyCount);

        _keys = new AddressAsKey[KeyCount];
        _accounts = new Account[KeyCount];

        var random = new Random(42);
        for (int i = 0; i < KeyCount; i++)
        {
            var addressBytes = new byte[20];
            random.NextBytes(addressBytes);
            Address address = new Address(addressBytes);
            _keys[i] = address;
            _accounts[i] = Build.An.Account.WithBalance((UInt256)i).TestObject;

            _lru.Set(_keys[i], _accounts[i]);
            _clock.Set(_keys[i], _accounts[i]);
            _assoc.Set(in _keys[i], _accounts[i]);
        }

        var missBytes = new byte[20];
        random.NextBytes(missBytes);
        _missKey = new Address(missBytes);
    }

    // ==================== Get (Hit) ====================

    [Benchmark]
    public Account? LruCache_Get_Hit() => _lru.Get(_keys[500]);

    [Benchmark]
    public Account? ClockCache_Get_Hit() => _clock.Get(_keys[500]);

    [Benchmark]
    public Account? AssociativeCache_Get_Hit() => _assoc.Get(in _keys[500]);

    // ==================== Get (Miss) ====================

    [Benchmark]
    public Account? LruCache_Get_Miss() => _lru.Get(_missKey);

    [Benchmark]
    public Account? ClockCache_Get_Miss() => _clock.Get(_missKey);

    [Benchmark]
    public Account? AssociativeCache_Get_Miss() => _assoc.Get(in _missKey);

    // ==================== Set (Existing Key) ====================

    [Benchmark]
    public void LruCache_Set_Existing() => _lru.Set(_keys[500], _accounts[500]);

    [Benchmark]
    public void ClockCache_Set_Existing() => _clock.Set(_keys[500], _accounts[500]);

    [Benchmark]
    public void AssociativeCache_Set_Existing() => _assoc.Set(in _keys[500], _accounts[500]);

    // ==================== Delete ====================

    private int _deleteIndex;

    [Benchmark]
    public bool LruCache_Delete()
    {
        int idx = _deleteIndex % KeyCount;
        bool result = _lru.Delete(_keys[idx]);
        // Re-insert so subsequent iterations still have something to delete
        _lru.Set(_keys[idx], _accounts[idx]);
        _deleteIndex++;
        return result;
    }

    [Benchmark]
    public bool ClockCache_Delete()
    {
        int idx = _deleteIndex % KeyCount;
        bool result = _clock.Delete(_keys[idx]);
        _clock.Set(_keys[idx], _accounts[idx]);
        _deleteIndex++;
        return result;
    }

    [Benchmark]
    public bool AssociativeCache_Delete()
    {
        int idx = _deleteIndex % KeyCount;
        bool result = _assoc.Delete(in _keys[idx]);
        _assoc.Set(in _keys[idx], _accounts[idx]);
        _deleteIndex++;
        return result;
    }
}

/// <summary>
/// Throughput comparison: mixed read/write workload (90% reads, 10% writes) and read-only.
/// </summary>
[MemoryDiagnoser]
public class AssociativeCacheMixedWorkloadBenchmarks
{
    private LruCache<AddressAsKey, Account> _lru = null!;
    private ClockCache<AddressAsKey, Account> _clock = null!;
    private AssociativeCache<AddressAsKey, Account> _assoc = null!;

    private AddressAsKey[] _keys = null!;
    private Account[] _accounts = null!;

    private const int KeyCount = 10000;
    private const int OperationsPerInvoke = 1000;

    [GlobalSetup]
    public void Setup()
    {
        _lru = new LruCache<AddressAsKey, Account>(KeyCount, "benchmark");
        _clock = new ClockCache<AddressAsKey, Account>(KeyCount);
        _assoc = new AssociativeCache<AddressAsKey, Account>(KeyCount);

        _keys = new AddressAsKey[KeyCount];
        _accounts = new Account[KeyCount];

        var random = new Random(42);
        for (int i = 0; i < KeyCount; i++)
        {
            var addressBytes = new byte[20];
            random.NextBytes(addressBytes);
            Address address = new Address(addressBytes);
            _keys[i] = address;
            _accounts[i] = Build.An.Account.WithBalance((UInt256)i).TestObject;

            _lru.Set(_keys[i], _accounts[i]);
            _clock.Set(_keys[i], _accounts[i]);
            _assoc.Set(in _keys[i], _accounts[i]);
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvoke)]
    public int LruCache_MixedWorkload_90Read_10Write()
    {
        int hits = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            int keyIndex = i % KeyCount;
            if (i % 10 == 0)
            {
                _lru.Set(_keys[keyIndex], _accounts[keyIndex]);
            }
            else
            {
                if (_lru.Get(_keys[keyIndex]) is not null)
                    hits++;
            }
        }
        return hits;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int ClockCache_MixedWorkload_90Read_10Write()
    {
        int hits = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            int keyIndex = i % KeyCount;
            if (i % 10 == 0)
            {
                _clock.Set(_keys[keyIndex], _accounts[keyIndex]);
            }
            else
            {
                if (_clock.Get(_keys[keyIndex]) is not null)
                    hits++;
            }
        }
        return hits;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int AssociativeCache_MixedWorkload_90Read_10Write()
    {
        int hits = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            int keyIndex = i % KeyCount;
            if (i % 10 == 0)
            {
                _assoc.Set(in _keys[keyIndex], _accounts[keyIndex]);
            }
            else
            {
                if (_assoc.Get(in _keys[keyIndex]) is not null)
                    hits++;
            }
        }
        return hits;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int LruCache_ReadOnly()
    {
        int hits = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            int keyIndex = i % KeyCount;
            if (_lru.Get(_keys[keyIndex]) is not null)
                hits++;
        }
        return hits;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int ClockCache_ReadOnly()
    {
        int hits = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            int keyIndex = i % KeyCount;
            if (_clock.Get(_keys[keyIndex]) is not null)
                hits++;
        }
        return hits;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int AssociativeCache_ReadOnly()
    {
        int hits = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            int keyIndex = i % KeyCount;
            if (_assoc.Get(in _keys[keyIndex]) is not null)
                hits++;
        }
        return hits;
    }
}

/// <summary>
/// Cache quality comparison — measures effective hit rate under uniform and Zipf-like access patterns.
/// </summary>
public class AssociativeCacheHitRateBenchmarks
{
    private LruCache<AddressAsKey, Account> _lru = null!;
    private ClockCache<AddressAsKey, Account> _clock = null!;
    private AssociativeCache<AddressAsKey, Account> _assoc = null!;

    private AddressAsKey[] _keys = null!;
    private Account[] _accounts = null!;
    private int[] _zipfIndices = null!;

    [Params(500, 1000, 5000, 10000)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _lru = new LruCache<AddressAsKey, Account>(KeyCount, "benchmark");
        _clock = new ClockCache<AddressAsKey, Account>(KeyCount);
        _assoc = new AssociativeCache<AddressAsKey, Account>(KeyCount);

        _keys = new AddressAsKey[KeyCount];
        _accounts = new Account[KeyCount];

        var random = new Random(42);
        for (int i = 0; i < KeyCount; i++)
        {
            var addressBytes = new byte[20];
            random.NextBytes(addressBytes);
            Address address = new Address(addressBytes);
            _keys[i] = address;
            _accounts[i] = Build.An.Account.WithBalance((UInt256)i).TestObject;

            _lru.Set(_keys[i], _accounts[i]);
            _clock.Set(_keys[i], _accounts[i]);
            _assoc.Set(in _keys[i], _accounts[i]);
        }

        // Pre-compute Zipf-like indices: 80% of accesses go to top 20% of keys
        int hotCount = Math.Max(1, KeyCount / 5);
        _zipfIndices = new int[KeyCount];
        for (int i = 0; i < KeyCount; i++)
        {
            // 80% chance to pick from hot set (indices 0..hotCount-1)
            _zipfIndices[i] = random.NextDouble() < 0.8
                ? random.Next(hotCount)
                : random.Next(KeyCount);
        }
    }

    // ==================== Uniform hit rate ====================

    [Benchmark]
    public double LruCache_HitRate_Uniform()
    {
        int hits = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_lru.Get(_keys[i]) is not null)
                hits++;
        }
        return (double)hits / KeyCount * 100;
    }

    [Benchmark]
    public double ClockCache_HitRate_Uniform()
    {
        int hits = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_clock.Get(_keys[i]) is not null)
                hits++;
        }
        return (double)hits / KeyCount * 100;
    }

    [Benchmark]
    public double AssociativeCache_HitRate_Uniform()
    {
        int hits = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            if (_assoc.Get(in _keys[i]) is not null)
                hits++;
        }
        return (double)hits / KeyCount * 100;
    }

    // ==================== Zipf-like hit rate ====================

    [Benchmark]
    public double LruCache_HitRate_Zipf()
    {
        int hits = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            int idx = _zipfIndices[i];
            if (_lru.Get(_keys[idx]) is not null)
                hits++;
        }
        return (double)hits / KeyCount * 100;
    }

    [Benchmark]
    public double ClockCache_HitRate_Zipf()
    {
        int hits = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            int idx = _zipfIndices[i];
            if (_clock.Get(_keys[idx]) is not null)
                hits++;
        }
        return (double)hits / KeyCount * 100;
    }

    [Benchmark]
    public double AssociativeCache_HitRate_Zipf()
    {
        int hits = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            int idx = _zipfIndices[i];
            if (_assoc.Get(in _keys[idx]) is not null)
                hits++;
        }
        return (double)hits / KeyCount * 100;
    }
}

/// <summary>
/// Multi-threaded throughput: mixed read/write workload across N threads.
/// </summary>
[MemoryDiagnoser]
public class AssociativeCacheConcurrencyBenchmarks
{
    private LruCache<AddressAsKey, Account> _lru = null!;
    private ClockCache<AddressAsKey, Account> _clock = null!;
    private AssociativeCache<AddressAsKey, Account> _assoc = null!;

    private AddressAsKey[] _keys = null!;
    private Account[] _accounts = null!;

    [Params(1, 2, 4, 8)]
    public int ThreadCount { get; set; }

    private const int KeyCount = 10000;
    private const int OpsPerThread = 1000;

    [GlobalSetup]
    public void Setup()
    {
        _lru = new LruCache<AddressAsKey, Account>(KeyCount, "benchmark");
        _clock = new ClockCache<AddressAsKey, Account>(KeyCount);
        _assoc = new AssociativeCache<AddressAsKey, Account>(KeyCount);

        _keys = new AddressAsKey[KeyCount];
        _accounts = new Account[KeyCount];

        var random = new Random(42);
        for (int i = 0; i < KeyCount; i++)
        {
            var addressBytes = new byte[20];
            random.NextBytes(addressBytes);
            Address address = new Address(addressBytes);
            _keys[i] = address;
            _accounts[i] = Build.An.Account.WithBalance((UInt256)i).TestObject;

            _lru.Set(_keys[i], _accounts[i]);
            _clock.Set(_keys[i], _accounts[i]);
            _assoc.Set(in _keys[i], _accounts[i]);
        }
    }

    [Benchmark]
    public long LruCache_ConcurrentMixed()
    {
        long total = 0;
        Thread[] threads = new Thread[ThreadCount];
        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                long ops = 0;
                int offset = threadId * OpsPerThread;
                for (int i = 0; i < OpsPerThread; i++)
                {
                    int keyIndex = (offset + i) % KeyCount;
                    if (i % 10 == 0)
                    {
                        _lru.Set(_keys[keyIndex], _accounts[keyIndex]);
                    }
                    else
                    {
                        if (_lru.Get(_keys[keyIndex]) is not null)
                            ops++;
                    }
                }
                Interlocked.Add(ref total, ops);
            });
        }
        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) thread.Join();
        return total;
    }

    [Benchmark]
    public long ClockCache_ConcurrentMixed()
    {
        long total = 0;
        Thread[] threads = new Thread[ThreadCount];
        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                long ops = 0;
                int offset = threadId * OpsPerThread;
                for (int i = 0; i < OpsPerThread; i++)
                {
                    int keyIndex = (offset + i) % KeyCount;
                    if (i % 10 == 0)
                    {
                        _clock.Set(_keys[keyIndex], _accounts[keyIndex]);
                    }
                    else
                    {
                        if (_clock.Get(_keys[keyIndex]) is not null)
                            ops++;
                    }
                }
                Interlocked.Add(ref total, ops);
            });
        }
        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) thread.Join();
        return total;
    }

    [Benchmark]
    public long AssociativeCache_ConcurrentMixed()
    {
        long total = 0;
        Thread[] threads = new Thread[ThreadCount];
        for (int t = 0; t < ThreadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                long ops = 0;
                int offset = threadId * OpsPerThread;
                for (int i = 0; i < OpsPerThread; i++)
                {
                    int keyIndex = (offset + i) % KeyCount;
                    if (i % 10 == 0)
                    {
                        _assoc.Set(in _keys[keyIndex], _accounts[keyIndex]);
                    }
                    else
                    {
                        if (_assoc.Get(in _keys[keyIndex]) is not null)
                            ops++;
                    }
                }
                Interlocked.Add(ref total, ops);
            });
        }
        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) thread.Join();
        return total;
    }
}
