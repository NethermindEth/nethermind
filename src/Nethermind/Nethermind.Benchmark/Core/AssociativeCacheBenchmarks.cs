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
/// Shared setup for LruCache / ClockCache / AssociativeCache comparison benchmarks.
/// </summary>
public abstract class CacheBenchmarkBase
{
    protected LruCache<AddressAsKey, Account> _lru = null!;
    protected ClockCache<AddressAsKey, Account> _clock = null!;
    protected AssociativeCache<AddressAsKey, Account> _assoc = null!;
    protected AddressAsKey[] _keys = null!;
    protected Account[] _accounts = null!;

    /// <summary>
    /// Creates and pre-fills all three caches with <paramref name="keyCount"/> random entries.
    /// Returns the <see cref="Random"/> instance so callers can draw additional values from the
    /// same deterministic sequence.
    /// </summary>
    protected Random SetupCaches(int keyCount)
    {
        _lru = new LruCache<AddressAsKey, Account>(keyCount, "benchmark");
        _clock = new ClockCache<AddressAsKey, Account>(keyCount);
        _assoc = new AssociativeCache<AddressAsKey, Account>(keyCount);

        _keys = new AddressAsKey[keyCount];
        _accounts = new Account[keyCount];

        Random random = new(42);
        for (int i = 0; i < keyCount; i++)
        {
            byte[] addressBytes = new byte[20];
            random.NextBytes(addressBytes);
            Address address = new(addressBytes);
            _keys[i] = address;
            _accounts[i] = Build.An.Account.WithBalance((UInt256)i).TestObject;

            _lru.Set(_keys[i], _accounts[i]);
            _clock.Set(_keys[i], _accounts[i]);
            _assoc.Set(in _keys[i], _accounts[i]);
        }

        return random;
    }
}

/// <summary>
/// Single-operation latency comparison: LruCache vs ClockCache vs AssociativeCache.
/// </summary>
[MemoryDiagnoser]
public class AssociativeCacheSingleOpBenchmarks : CacheBenchmarkBase
{
    private AddressAsKey _missKey;
    private int _deleteIndex;

    [Params(1000)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = SetupCaches(KeyCount);

        byte[] missBytes = new byte[20];
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

    [Benchmark]
    public bool AssociativeCache_TryGetNoRefresh_Hit() => _assoc.TryGetNoRefresh(in _keys[500], out _);

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
public class AssociativeCacheMixedWorkloadBenchmarks : CacheBenchmarkBase
{
    private const int KeyCount = 10000;
    private const int OperationsPerInvoke = 1000;

    [GlobalSetup]
    public void Setup() => SetupCaches(KeyCount);

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
/// Multi-threaded throughput: mixed read/write workload across N threads.
/// </summary>
[MemoryDiagnoser]
public class AssociativeCacheConcurrencyBenchmarks : CacheBenchmarkBase
{
    private const int KeyCount = 10000;
    private const int OpsPerThread = 1000;

    [Params(1, 2, 4, 8)]
    public int ThreadCount { get; set; }

    [GlobalSetup]
    public void Setup() => SetupCaches(KeyCount);

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
